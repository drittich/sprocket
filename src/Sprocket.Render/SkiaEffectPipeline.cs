using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render;

/// <summary>
/// The slice's GPU effect stage (PLAN.md step 7, ARCHITECTURE.md §7): turns a decoded RGBA frame plus a
/// clip's resolved effect chain into one drawn image, with each built-in effect realised as an
/// <see cref="SKRuntimeEffect"/> (SkSL) fragment shader. The effects are <em>chained as a shader graph</em>
/// — effect N's <c>src</c> child is effect N-1's output shader, with the decoded image's shader at the root —
/// so the whole stack resolves in minimal GPU passes rather than N offscreen round-trips (§7).
/// </summary>
/// <remarks>
/// <para>The two SkSL programs are compiled once in the constructor and reused for every frame; only the
/// small per-frame shader/uniform objects are allocated (the bounded, non-pixel allocation §7 acknowledges).
/// When the effect list is empty the frame is drawn with a plain <see cref="SKCanvas.DrawImage(SKImage, SKRect, SKSamplingOptions)"/>
/// so the no-effects hot path stays exactly as allocation-clean as <see cref="FramePresenter"/> measured it.</para>
/// <para>Not thread-safe: a single instance is used from one render thread (inside the Avalonia Skia lease, or a
/// test's offscreen surface). Unknown effect type ids are skipped (pass-through), so a plugin effect with no
/// Render binding degrades to a no-op rather than throwing.</para>
/// </remarks>
public sealed class SkiaEffectPipeline : IDisposable
{
    // Brightness — multiply the (premultiplied) colour by amount, leaving alpha. amount 1.0 = unchanged.
    private const string BrightnessSksl = @"
uniform shader src;
uniform float amount;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    return half4(c.rgb * amount, c.a);
}";

    // Fade — scale the whole premultiplied pixel by opacity. Over the cleared background this reads as
    // fade-to-black; composited over a lower layer it is a correct premultiplied fade-out. opacity 1.0 = opaque.
    private const string FadeSksl = @"
uniform shader src;
uniform float opacity;
half4 main(float2 coord) {
    return src.eval(coord) * opacity;
}";

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);

    private readonly SKRuntimeEffect _brightness;
    private readonly SKRuntimeEffect _fade;
    private readonly SKPaint _paint = new();
    private readonly List<SKShader> _scratch = new(); // shaders built for the current draw, disposed after it
    private bool _disposed;

    /// <summary>Compiles the built-in effect shaders. Throws if either SkSL program fails to compile.</summary>
    public SkiaEffectPipeline()
    {
        _brightness = SKRuntimeEffect.CreateShader(BrightnessSksl, out string brightnessErr)
            ?? throw new InvalidOperationException($"Brightness SkSL failed to compile: {brightnessErr}");
        _fade = SKRuntimeEffect.CreateShader(FadeSksl, out string fadeErr)
            ?? throw new InvalidOperationException($"Fade SkSL failed to compile: {fadeErr}");
    }

    /// <summary>
    /// Clears <paramref name="bounds"/> to <paramref name="background"/> and draws the
    /// <paramref name="width"/>×<paramref name="height"/> RGBA8888 frame at <paramref name="pixels"/>
    /// (stride <paramref name="rowBytes"/>) scaled-to-fit (letterboxed), with <paramref name="effects"/>
    /// applied as a chained shader graph. The native pixels are wrapped, not copied (§1); they must remain
    /// valid for the call. Does nothing if the bounds or frame are degenerate.
    /// </summary>
    public void Present(
        SKCanvas canvas,
        SKRect bounds,
        nint pixels,
        int rowBytes,
        int width,
        int height,
        IReadOnlyList<ResolvedEffect> effects,
        SKColor background)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        canvas.Clear(background);

        if (pixels == 0 || width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        SKRect dest = FramePresenter.ComputeFitRect(bounds, width, height);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKImage image = SKImage.FromPixels(info, pixels, rowBytes);

        // No effects → the plain fit-draw, identical to the allocation-clean step-4 path.
        if (effects is null || effects.Count == 0)
        {
            canvas.DrawImage(image, dest, Sampling);
            return;
        }

        // Root of the chain: the decoded image as a shader, mapped into the destination rectangle so the
        // runtime effects sample it in canvas space (a uniform fit scale, hence one factor for both axes).
        float scale = dest.Width / width;
        SKMatrix localMatrix = SKMatrix.CreateScaleTranslation(scale, scale, dest.Left, dest.Top);

        _scratch.Clear();
        SKShader shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, localMatrix);
        _scratch.Add(shader);

        foreach (ResolvedEffect effect in effects)
        {
            SKShader? next = BuildEffectShader(effect, shader);
            if (next is null)
                continue; // unknown effect id: pass through unchanged
            shader = next;
            _scratch.Add(shader);
        }

        _paint.Shader = shader;
        canvas.DrawRect(dest, _paint);
        _paint.Shader = null;

        // The draw has consumed the shader graph; release the per-frame shader objects (the image is freed by
        // the using above). Intermediate child shaders are not auto-disposed by their parents, so dispose all.
        foreach (SKShader s in _scratch)
            s.Dispose();
        _scratch.Clear();
    }

    /// <summary>
    /// Builds the shader for one effect wrapping <paramref name="src"/> (the previous stage), or
    /// <see langword="null"/> for an effect type with no Render binding (skipped).
    /// </summary>
    private SKShader? BuildEffectShader(ResolvedEffect effect, SKShader src)
    {
        switch (effect.EffectTypeId)
        {
            case EffectTypeIds.Brightness:
            {
                var uniforms = new SKRuntimeEffectUniforms(_brightness)
                {
                    ["amount"] = (float)effect.Get(EffectParamNames.Amount, 1.0),
                };
                var children = new SKRuntimeEffectChildren(_brightness) { ["src"] = src };
                return _brightness.ToShader(uniforms, children);
            }

            case EffectTypeIds.Fade:
            {
                float opacity = (float)Math.Clamp(effect.Get(EffectParamNames.Opacity, 1.0), 0.0, 1.0);
                var uniforms = new SKRuntimeEffectUniforms(_fade) { ["opacity"] = opacity };
                var children = new SKRuntimeEffectChildren(_fade) { ["src"] = src };
                return _fade.ToShader(uniforms, children);
            }

            default:
                return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (SKShader s in _scratch)
            s.Dispose();
        _scratch.Clear();
        _paint.Dispose();
        _brightness.Dispose();
        _fade.Dispose();
    }
}
