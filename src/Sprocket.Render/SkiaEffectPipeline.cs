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

    // Color — exposure/contrast/saturation/vibrance on premultiplied colour (PLAN.md steps 16/34). All
    // operations are premultiplied-safe: where alpha is 0 the result stays 0, so they compose correctly over
    // the chain (and a shrunk Transform's transparent surround stays transparent). Vibrance is saturation
    // weighted toward already-muted colours (its boost fades as a pixel's own saturation rises), so skin
    // tones and saturated areas move less than flat ones — the Lightroom/Lumetri behaviour. rgb is clamped
    // to [0, a] to stay valid premult.
    private const string ColorSksl = @"
uniform shader src;
uniform float exposure;
uniform float contrast;
uniform float saturation;
uniform float vibrance;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    float a = c.a;
    float3 rgb = float3(c.rgb) * exp2(exposure);
    float mid = 0.5 * a;
    rgb = (rgb - mid) * contrast + mid;
    float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    rgb = mix(float3(luma), rgb, saturation);
    float mx = max(rgb.r, max(rgb.g, rgb.b));
    float mn = min(rgb.r, min(rgb.g, rgb.b));
    float satNow = mx <= 0.0 ? 0.0 : (mx - mn) / mx;
    rgb = mix(float3(luma), rgb, 1.0 + vibrance * (1.0 - satNow));
    rgb = clamp(rgb, 0.0, a);
    return half4(half3(rgb), c.a);
}";

    // Transform — scale/rotate/position the layer (PLAN.md step 16). The C# side builds the forward transform
    // around the anchor in canvas space, inverts it, and passes the inverse affine (m = 2×2, t = translation)
    // so the shader maps each output coordinate back to a source coordinate before sampling. The root image
    // shader uses Decal tiling when a transform is present, so coordinates outside the frame sample transparent
    // (a shrunk layer reveals the background rather than smearing edge pixels). opacity scales the result.
    private const string TransformSksl = @"
uniform shader src;
uniform float4 m;   // inverse affine: (scaleX, skewX, skewY, scaleY)
uniform float2 t;   // inverse translation
uniform float opacity;
half4 main(float2 coord) {
    float2 c = float2(m.x * coord.x + m.y * coord.y + t.x,
                      m.z * coord.x + m.w * coord.y + t.y);
    return src.eval(c) * opacity;
}";

    // Transition shaders (PLAN.md step 25): each samples two child shaders — `from` (outgoing clip) and `to`
    // (incoming clip), already folded through their own effect chains — and blends them per `progress` (0 = full
    // from, 1 = full to). All operate on premultiplied colour, so they compose correctly over the lower layers.

    // Cross dissolve — a straight linear mix; mix() of premultiplied colour is the correct A·(1-p) + B·p dissolve.
    private const string CrossDissolveSksl = @"
uniform shader from;
uniform shader to;
uniform float progress;
half4 main(float2 coord) {
    return mix(from.eval(coord), to.eval(coord), progress);
}";

    // Dip to black — the outgoing clip fades out over the first half, the incoming fades in over the second; at the
    // midpoint both weights are 0, so the frame is black (premultiplied black is 0).
    private const string DipToBlackSksl = @"
uniform shader from;
uniform shader to;
uniform float progress;
half4 main(float2 coord) {
    float a = clamp(1.0 - 2.0 * progress, 0.0, 1.0);
    float b = clamp(2.0 * progress - 1.0, 0.0, 1.0);
    return from.eval(coord) * a + to.eval(coord) * b;
}";

    // Dip to white — like dip-to-black but the midpoint is opaque white. Frames are opaque, so premultiplied white
    // is half4(1); mixing toward it over the first half and away over the second produces the dip.
    private const string DipToWhiteSksl = @"
uniform shader from;
uniform shader to;
uniform float progress;
half4 main(float2 coord) {
    half4 white = half4(1.0);
    float t = progress * 2.0;
    if (t < 1.0) return mix(from.eval(coord), white, t);
    return mix(white, to.eval(coord), t - 1.0);
}";

    // Wipe — the incoming clip is revealed from the left as progress advances; `bounds` (left,top,width,height)
    // normalises the canvas x into [0,1] across the frame, and a small soft edge avoids a hard aliased seam.
    private const string WipeSksl = @"
uniform shader from;
uniform shader to;
uniform float progress;
uniform float4 bounds;
half4 main(float2 coord) {
    float x = (coord.x - bounds.x) / max(bounds.z, 1.0);
    float edge = 0.01;
    float w = smoothstep(progress - edge, progress + edge, x);
    return mix(to.eval(coord), from.eval(coord), w);
}";

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);

    // ── Registered shader effects (PLAN.md step 33, ARCHITECTURE.md §13) ─────────────────────────────
    // The process-wide registry of IVideoEffect implementations — built-in shader effects (ACES) plus
    // plugin-contributed ones — shared by every pipeline instance (preview, export, thumbnails) so an
    // effect registered at plugin-load time renders everywhere. Each *instance* keeps its own compiled
    // SKRuntimeEffect cache (below), matching the existing one-pipeline-per-thread ownership model.
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<string, IVideoEffect> Registry = new(StringComparer.Ordinal);

    static SkiaEffectPipeline()
    {
        RegisterEffect(new Effects.AcesFilmicEffect());
        // The colour grading toolset (PLAN.md step 34) — registry effects like ACES, not pipeline cases.
        RegisterEffect(new Effects.WhiteBalanceEffect());
        RegisterEffect(new Effects.ColorWheelsEffect());
        RegisterEffect(new Effects.CurvesEffect());
        RegisterEffect(new Effects.HslQualifierEffect());
    }

    /// <summary>
    /// Registers (or replaces) a shader-backed effect for all pipeline instances. Compiles the effect's SkSL
    /// once up front and throws <see cref="InvalidOperationException"/> on a compile error, so a broken
    /// plugin fails loudly at load time instead of mid-draw.
    /// </summary>
    public static void RegisterEffect(IVideoEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        CompileRegistered(effect).Dispose(); // validate the SkSL up front
        lock (RegistryGate)
            Registry[effect.Descriptor.Id] = effect;
    }

    /// <summary>Removes a registered plugin effect (built-in ids are refused); its uses degrade to pass-through.
    /// Returns whether it was present.</summary>
    public static bool UnregisterEffect(string effectTypeId)
    {
        if (effectTypeId.StartsWith("builtin.", StringComparison.Ordinal))
            return false;
        lock (RegistryGate)
            return Registry.Remove(effectTypeId);
    }

    private static IVideoEffect? FindRegistered(string effectTypeId)
    {
        lock (RegistryGate)
            return Registry.GetValueOrDefault(effectTypeId);
    }

    private static SKRuntimeEffect CompileRegistered(IVideoEffect effect) =>
        SKRuntimeEffect.CreateShader(effect.SkslSource, out string err)
            ?? throw new InvalidOperationException($"Effect '{effect.Descriptor.Id}' SkSL failed to compile: {err}");

    /// <summary>Adapts <see cref="SKRuntimeEffectUniforms"/> to the GPU-agnostic Core seam.</summary>
    private sealed class SkUniformWriter(SKRuntimeEffectUniforms uniforms) : IUniformWriter
    {
        public void Set(string name, float value) => uniforms[name] = value;
        public void Set(string name, float[] values) => uniforms[name] = values;
    }

    private sealed record CachedRegisteredEffect(IVideoEffect Source, SKRuntimeEffect Compiled);

    // Per-instance compiled cache for registered effects, keyed by effect type id. Entries are invalidated
    // by reference-comparing the registered IVideoEffect, so a re-registered (reloaded) plugin recompiles.
    private readonly Dictionary<string, CachedRegisteredEffect> _registeredCache = new(StringComparer.Ordinal);

    private readonly SKRuntimeEffect _brightness;
    private readonly SKRuntimeEffect _fade;
    private readonly SKRuntimeEffect _color;
    private readonly SKRuntimeEffect _transform;
    private readonly SKRuntimeEffect _crossDissolve;
    private readonly SKRuntimeEffect _dipToBlack;
    private readonly SKRuntimeEffect _dipToWhite;
    private readonly SKRuntimeEffect _wipe;
    private readonly SKPaint _paint = new();
    private readonly List<SKShader> _scratch = new(); // shaders built for the current draw, disposed after it
    private bool _disposed;

    /// <summary>Compiles the built-in effect and transition shaders. Throws if any SkSL program fails to compile.</summary>
    public SkiaEffectPipeline()
    {
        _brightness = SKRuntimeEffect.CreateShader(BrightnessSksl, out string brightnessErr)
            ?? throw new InvalidOperationException($"Brightness SkSL failed to compile: {brightnessErr}");
        _fade = SKRuntimeEffect.CreateShader(FadeSksl, out string fadeErr)
            ?? throw new InvalidOperationException($"Fade SkSL failed to compile: {fadeErr}");
        _color = SKRuntimeEffect.CreateShader(ColorSksl, out string colorErr)
            ?? throw new InvalidOperationException($"Color SkSL failed to compile: {colorErr}");
        _transform = SKRuntimeEffect.CreateShader(TransformSksl, out string transformErr)
            ?? throw new InvalidOperationException($"Transform SkSL failed to compile: {transformErr}");
        _crossDissolve = SKRuntimeEffect.CreateShader(CrossDissolveSksl, out string crossErr)
            ?? throw new InvalidOperationException($"Cross-dissolve SkSL failed to compile: {crossErr}");
        _dipToBlack = SKRuntimeEffect.CreateShader(DipToBlackSksl, out string dipBlackErr)
            ?? throw new InvalidOperationException($"Dip-to-black SkSL failed to compile: {dipBlackErr}");
        _dipToWhite = SKRuntimeEffect.CreateShader(DipToWhiteSksl, out string dipWhiteErr)
            ?? throw new InvalidOperationException($"Dip-to-white SkSL failed to compile: {dipWhiteErr}");
        _wipe = SKRuntimeEffect.CreateShader(WipeSksl, out string wipeErr)
            ?? throw new InvalidOperationException($"Wipe SkSL failed to compile: {wipeErr}");
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
        SKColor background,
        bool hasAlpha = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        canvas.Clear(background);

        if (pixels == 0 || width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        SKRect dest = FramePresenter.ComputeFitRect(bounds, width, height);
        DrawLayer(canvas, dest, pixels, rowBytes, width, height, effects, hasAlpha: hasAlpha);
    }

    /// <summary>
    /// Draws one decoded RGBA8888 layer (<paramref name="width"/>×<paramref name="height"/> at
    /// <paramref name="pixels"/>, stride <paramref name="rowBytes"/>) into <paramref name="dest"/> with its
    /// <paramref name="effects"/> chain, compositing onto whatever is already on the canvas with
    /// <paramref name="opacity"/> and <paramref name="blend"/> — <b>without clearing</b>. This is the
    /// per-layer primitive shared by the single-layer preview (<see cref="Present"/>, which clears first) and
    /// the export path, which clears once then draws each resolved layer bottom→top. The native pixels are
    /// wrapped, not copied (§1), and must remain valid for the call. Does nothing for a degenerate layer.
    /// <para>When <paramref name="hasAlpha"/> is set the buffer is treated as straight (unpremultiplied) RGBA — the
    /// format swscale emits for alpha-channel sources (PLAN.md step 26) — so Skia premultiplies and composites it
    /// source-over the layers beneath, revealing them through transparent pixels. When clear (the default) the buffer
    /// is <see cref="SKAlphaType.Opaque"/>: the alpha bytes are ignored and the layer fully replaces what is under it,
    /// keeping the opaque hot path exactly as measured.</para>
    /// </summary>
    public void DrawLayer(
        SKCanvas canvas,
        SKRect dest,
        nint pixels,
        int rowBytes,
        int width,
        int height,
        IReadOnlyList<ResolvedEffect> effects,
        double opacity = 1.0,
        SKBlendMode blend = SKBlendMode.SrcOver,
        bool hasAlpha = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        if (pixels == 0 || width <= 0 || height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return;

        SKAlphaType alphaType = hasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, alphaType);
        using SKImage image = SKImage.FromPixels(info, pixels, rowBytes);
        DrawImageLayer(canvas, dest, image, effects, opacity, blend);
    }

    /// <summary>
    /// Draws an already-built <see cref="SKImage"/> into <paramref name="dest"/> with its <paramref name="effects"/>
    /// chain, compositing onto the canvas with <paramref name="opacity"/> and <paramref name="blend"/> (no clear).
    /// The shared per-layer primitive: <see cref="DrawLayer"/> wraps decoded native pixels and calls it, while
    /// <see cref="DrawGenerator"/> and the adjustment-layer path build their <see cref="SKImage"/> first. The
    /// image must remain valid for the call.
    /// </summary>
    public void DrawImageLayer(
        SKCanvas canvas,
        SKRect dest,
        SKImage image,
        IReadOnlyList<ResolvedEffect> effects,
        double opacity = 1.0,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(image);

        if (image.Width <= 0 || image.Height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return;

        byte alpha = (byte)Math.Clamp(opacity * 255.0, 0, 255);

        // No effects → a plain image draw (the allocation-clean step-4 path). Track opacity/blend still apply.
        if (effects is null || effects.Count == 0)
        {
            _paint.Color = SKColors.White.WithAlpha(alpha); // paint alpha modulates the image when shader is null
            _paint.BlendMode = blend;
            canvas.DrawImage(image, dest, Sampling, _paint);
            ResetPaint();
            return;
        }

        _scratch.Clear();
        SKShader shader = BuildChainShader(image, dest, effects);

        _paint.Shader = shader;
        _paint.Color = SKColors.White.WithAlpha(alpha); // paint alpha modulates the shader output
        _paint.BlendMode = blend;
        canvas.DrawRect(dest, _paint);
        ResetPaint();
        DisposeScratch();
    }

    /// <summary>
    /// Composites a transition between two decoded layers into <paramref name="bounds"/> (PLAN.md step 25,
    /// ARCHITECTURE.md §7): each side — <paramref name="from"/> (outgoing) and <paramref name="to"/> (incoming) —
    /// is fit-letterboxed into the frame and folded through its own <paramref name="fromEffects"/>/
    /// <paramref name="toEffects"/> chain, then the two are combined by the transition's two-input shader at
    /// <paramref name="transition"/>'s progress and drawn with <paramref name="opacity"/>/<paramref name="blend"/>
    /// (the track's), <b>without clearing</b>. An unknown transition id degrades to a cross dissolve (mirroring the
    /// effect pass-through rule). The native images must remain valid for the call.
    /// </summary>
    public void DrawTransition(
        SKCanvas canvas,
        SKRect bounds,
        SKImage from,
        IReadOnlyList<ResolvedEffect> fromEffects,
        SKImage to,
        IReadOnlyList<ResolvedEffect> toEffects,
        ResolvedTransition transition,
        double opacity = 1.0,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(transition);

        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0
            || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        byte alpha = (byte)Math.Clamp(opacity * 255.0, 0, 255);

        // Each side is letterboxed into the full frame independently (so differently-sized clips both fit), then
        // the transition shader operates over the whole frame; Decal tiling makes a letterbox surround transparent.
        SKRect fromDest = FramePresenter.ComputeFitRect(bounds, from.Width, from.Height);
        SKRect toDest = FramePresenter.ComputeFitRect(bounds, to.Width, to.Height);

        _scratch.Clear();
        SKShader fromShader = BuildChainShader(from, fromDest, fromEffects, forceDecal: true);
        SKShader toShader = BuildChainShader(to, toDest, toEffects, forceDecal: true);
        SKShader combined = BuildTransitionShader(transition, fromShader, toShader, bounds);
        _scratch.Add(combined);

        _paint.Shader = combined;
        _paint.Color = SKColors.White.WithAlpha(alpha);
        _paint.BlendMode = blend;
        canvas.DrawRect(bounds, _paint);
        ResetPaint();
        DisposeScratch();
    }

    /// <summary>
    /// Builds the shader for one layer: the <paramref name="image"/> mapped into <paramref name="dest"/> as the
    /// root, folded through its <paramref name="effects"/> chain (each stage wrapping the previous). Every built
    /// shader is appended to <see cref="_scratch"/> (the caller clears it first and disposes via
    /// <see cref="DisposeScratch"/> after the draw). When a Transform is in the chain it can sample outside the
    /// frame, so Decal tiling reads that as transparent; <paramref name="forceDecal"/> forces the same for a
    /// transition side so a letterboxed frame's surround stays transparent instead of edge-clamping.
    /// </summary>
    private SKShader BuildChainShader(SKImage image, SKRect dest, IReadOnlyList<ResolvedEffect>? effects, bool forceDecal = false)
    {
        float scale = dest.Width / image.Width;
        SKMatrix localMatrix = SKMatrix.CreateScaleTranslation(scale, scale, dest.Left, dest.Top);
        SKShaderTileMode tile = forceDecal || (effects is not null && HasTransform(effects))
            ? SKShaderTileMode.Decal : SKShaderTileMode.Clamp;

        SKShader shader = image.ToShader(tile, tile, Sampling, localMatrix);
        _scratch.Add(shader);

        if (effects is not null)
        {
            foreach (ResolvedEffect effect in effects)
            {
                SKShader? next = BuildEffectShader(effect, shader, dest);
                if (next is null)
                    continue; // unknown effect id: pass through unchanged
                shader = next;
                _scratch.Add(shader);
            }
        }
        return shader;
    }

    /// <summary>Builds the two-input shader combining <paramref name="from"/>/<paramref name="to"/> for a transition
    /// at its progress; an unknown type id falls back to a cross dissolve. <paramref name="bounds"/> normalises a
    /// wipe's position across the frame.</summary>
    private SKShader BuildTransitionShader(ResolvedTransition transition, SKShader from, SKShader to, SKRect bounds)
    {
        float progress = (float)Math.Clamp(transition.Progress, 0.0, 1.0);
        switch (transition.TransitionTypeId)
        {
            case TransitionTypeIds.DipToBlack:
                return _dipToBlack.ToShader(
                    new SKRuntimeEffectUniforms(_dipToBlack) { ["progress"] = progress },
                    new SKRuntimeEffectChildren(_dipToBlack) { ["from"] = from, ["to"] = to });

            case TransitionTypeIds.DipToWhite:
                return _dipToWhite.ToShader(
                    new SKRuntimeEffectUniforms(_dipToWhite) { ["progress"] = progress },
                    new SKRuntimeEffectChildren(_dipToWhite) { ["from"] = from, ["to"] = to });

            case TransitionTypeIds.Wipe:
                return _wipe.ToShader(
                    new SKRuntimeEffectUniforms(_wipe)
                    {
                        ["progress"] = progress,
                        ["bounds"] = new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height },
                    },
                    new SKRuntimeEffectChildren(_wipe) { ["from"] = from, ["to"] = to });

            default: // cross dissolve — and unknown (plugin) ids degrade to it rather than dropping the layer
                return _crossDissolve.ToShader(
                    new SKRuntimeEffectUniforms(_crossDissolve) { ["progress"] = progress },
                    new SKRuntimeEffectChildren(_crossDissolve) { ["from"] = from, ["to"] = to });
        }
    }

    /// <summary>Disposes and clears the per-draw scratch shaders (intermediate children are not auto-disposed by
    /// their parents, so all must be released after the draw consumes them; the images are freed by the caller).</summary>
    private void DisposeScratch()
    {
        foreach (SKShader s in _scratch)
            s.Dispose();
        _scratch.Clear();
    }

    /// <summary>
    /// Draws a generator's procedural content (title/text, colour matte — PLAN.md step 19) into
    /// <paramref name="dest"/>, with its <paramref name="effects"/> chain and track <paramref name="opacity"/>/
    /// <paramref name="blend"/>. The generator is rendered into a fresh <paramref name="width"/>×<paramref name="height"/>
    /// surface (the sequence resolution) and then composited through the same per-layer path as a decoded frame, so
    /// generators carry effects and blend like any other layer. An unknown generator id draws nothing (pass-through).
    /// </summary>
    public void DrawGenerator(
        SKCanvas canvas,
        SKRect dest,
        ResolvedGenerator generator,
        int width,
        int height,
        IReadOnlyList<ResolvedEffect> effects,
        double opacity = 1.0,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(generator);

        if (width <= 0 || height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface? offscreen = SKSurface.Create(info);
        if (offscreen is null)
            return;

        RenderGeneratorContent(offscreen.Canvas, generator, width, height);
        offscreen.Canvas.Flush();
        using SKImage image = offscreen.Snapshot();
        DrawImageLayer(canvas, dest, image, effects, opacity, blend);
    }

    /// <summary>
    /// Applies an adjustment layer's <paramref name="effects"/> to the composite already drawn into
    /// <paramref name="surface"/> within <paramref name="dest"/>, blending the graded result back with
    /// <paramref name="opacity"/>/<paramref name="blend"/> (PLAN.md step 19, ARCHITECTURE.md §5). It snapshots the
    /// region beneath, runs the effect chain over it, and draws it back — so at full opacity it replaces the region
    /// with the graded version, and below full opacity it cross-fades the original with the grade.
    /// </summary>
    public void DrawAdjustment(
        SKSurface surface,
        SKRect dest,
        IReadOnlyList<ResolvedEffect> effects,
        double opacity = 1.0,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(surface);

        if (effects is null || effects.Count == 0 || dest.Width <= 0 || dest.Height <= 0)
            return;

        SKCanvas canvas = surface.Canvas;
        canvas.Flush();

        // Snapshot in surface (device) pixels: map the destination through the canvas's current matrix so a
        // translated/scaled preview canvas grabs the right region. For the export surface the matrix is identity,
        // so this is exactly round(dest). Effects then sample `beneath` (the lower composite), so e.g. a Color
        // grade on the adjustment layer regrades everything underneath it.
        SKRect deviceDest = canvas.TotalMatrix.MapRect(dest);
        SKRectI region = SKRectI.Round(deviceDest);
        using SKImage? beneath = surface.Snapshot(region);
        if (beneath is null)
            return;

        DrawImageLayer(canvas, dest, beneath, effects, opacity, blend);
    }

    /// <summary>Draws one generator's content into a transparent <paramref name="width"/>×<paramref name="height"/>
    /// canvas. Solid colour fills the frame; a title draws a background fill (often transparent) then centred text.
    /// Unknown generator ids leave the canvas transparent (a generator plugin with no Render binding is a no-op).</summary>
    private static void RenderGeneratorContent(SKCanvas canvas, ResolvedGenerator generator, int width, int height)
    {
        canvas.Clear(SKColors.Transparent);

        switch (generator.GeneratorTypeId)
        {
            case GeneratorTypeIds.SolidColor:
                canvas.Clear(ParseColor(generator.GetString(GeneratorParamNames.Color), SKColors.Black));
                break;

            case GeneratorTypeIds.Title:
            {
                canvas.Clear(ParseColor(generator.GetString(GeneratorParamNames.BackgroundColor), SKColors.Transparent));

                string text = generator.GetString(GeneratorParamNames.Text);
                if (string.IsNullOrEmpty(text))
                    break;

                float fontFraction = (float)generator.Get(GeneratorParamNames.FontSize, 0.12);
                float textSize = Math.Max(1f, fontFraction * height);
                using var font = new SKFont(SKTypeface.Default, textSize);
                using var paint = new SKPaint
                {
                    Color = ParseColor(generator.GetString(GeneratorParamNames.Color), SKColors.White),
                    IsAntialias = true,
                };
                // Centre the text: x at the frame centre (Center align), baseline placed so the glyph box is centred.
                font.MeasureText(text, out SKRect bounds);
                float baseline = height / 2f - bounds.MidY;
                canvas.DrawText(text, width / 2f, baseline, SKTextAlign.Center, font, paint);
                break;
            }

            // Unknown generator: leave transparent.
        }
    }

    /// <summary>Parses a <c>#AARRGGBB</c>/<c>#RRGGBB</c> colour string, falling back to <paramref name="fallback"/>.</summary>
    private static SKColor ParseColor(string value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out SKColor color) ? color : fallback;

    private void ResetPaint()
    {
        _paint.Shader = null;
        _paint.Color = SKColors.White;
        _paint.BlendMode = SKBlendMode.SrcOver;
    }

    private static bool HasTransform(IReadOnlyList<ResolvedEffect> effects)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].EffectTypeId == EffectTypeIds.Transform)
                return true;
        return false;
    }

    /// <summary>
    /// Builds the shader for one effect wrapping <paramref name="src"/> (the previous stage), or
    /// <see langword="null"/> for an effect type with no Render binding (skipped). <paramref name="dest"/> is
    /// the layer's canvas rectangle, needed to anchor the geometric <see cref="EffectTypeIds.Transform"/>.
    /// </summary>
    private SKShader? BuildEffectShader(ResolvedEffect effect, SKShader src, SKRect dest)
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

            case EffectTypeIds.Color:
            {
                var uniforms = new SKRuntimeEffectUniforms(_color)
                {
                    ["exposure"] = (float)effect.Get(EffectParamNames.Exposure, 0.0),
                    ["contrast"] = (float)Math.Max(0.0, effect.Get(EffectParamNames.Contrast, 1.0)),
                    ["saturation"] = (float)Math.Max(0.0, effect.Get(EffectParamNames.Saturation, 1.0)),
                    ["vibrance"] = (float)Math.Clamp(effect.Get(EffectParamNames.Vibrance, 0.0), -1.0, 1.0),
                };
                var children = new SKRuntimeEffectChildren(_color) { ["src"] = src };
                return _color.ToShader(uniforms, children);
            }

            case EffectTypeIds.Transform:
                return BuildTransformShader(effect, src, dest);

            default:
                return BuildRegisteredEffectShader(effect, src);
        }
    }

    /// <summary>
    /// Builds the shader for a registry-backed effect (built-in ACES or a plugin, PLAN.md step 33), or
    /// <see langword="null"/> to pass through when the id has no registration (e.g. a project referencing an
    /// uninstalled plugin) or the effect faults while binding — degrade, don't crash (§15).
    /// </summary>
    private SKShader? BuildRegisteredEffectShader(ResolvedEffect effect, SKShader src)
    {
        IVideoEffect? registered = FindRegistered(effect.EffectTypeId);
        if (registered is null)
        {
            if (_registeredCache.Remove(effect.EffectTypeId, out CachedRegisteredEffect? stale))
                stale.Compiled.Dispose(); // the plugin was unregistered (unloaded) since we last drew it
            return null;
        }

        try
        {
            if (!_registeredCache.TryGetValue(effect.EffectTypeId, out CachedRegisteredEffect? cached)
                || !ReferenceEquals(cached.Source, registered))
            {
                cached?.Compiled.Dispose();
                cached = new CachedRegisteredEffect(registered, CompileRegistered(registered));
                _registeredCache[effect.EffectTypeId] = cached;
            }

            var uniforms = new SKRuntimeEffectUniforms(cached.Compiled);
            registered.BindUniforms(effect, new SkUniformWriter(uniforms));
            var children = new SKRuntimeEffectChildren(cached.Compiled) { ["src"] = src };
            return cached.Compiled.ToShader(uniforms, children);
        }
        catch
        {
            return null; // a faulting effect passes through rather than killing the frame
        }
    }

    /// <summary>
    /// Builds the geometric transform shader: composes scale → rotate → position about the anchor (in canvas
    /// space), inverts it, and feeds the inverse affine to the SkSL so it can map output coordinates back to
    /// source coordinates. A degenerate (non-invertible, e.g. scale 0) transform draws nothing.
    /// </summary>
    private SKShader? BuildTransformShader(ResolvedEffect effect, SKShader src, SKRect dest)
    {
        double scale = effect.Get(EffectParamNames.Scale, 1.0);
        double posX = effect.Get(EffectParamNames.PositionX, 0.0);
        double posY = effect.Get(EffectParamNames.PositionY, 0.0);
        double rotation = effect.Get(EffectParamNames.Rotation, 0.0);
        double anchorX = effect.Get(EffectParamNames.AnchorX, 0.5);
        double anchorY = effect.Get(EffectParamNames.AnchorY, 0.5);
        float opacity = (float)Math.Clamp(effect.Get(EffectParamNames.Opacity, 1.0), 0.0, 1.0);

        // Anchor + position in canvas space (position is a fraction of the layer rectangle).
        float ax = dest.Left + (float)anchorX * dest.Width;
        float ay = dest.Top + (float)anchorY * dest.Height;
        float offX = (float)posX * dest.Width;
        float offY = (float)posY * dest.Height;

        // Forward: translate anchor→origin, scale, rotate, translate back + position offset.
        SKMatrix forward = SKMatrix.CreateTranslation(-ax, -ay);
        forward = SKMatrix.Concat(SKMatrix.CreateScale((float)scale, (float)scale), forward);
        forward = SKMatrix.Concat(SKMatrix.CreateRotationDegrees((float)rotation), forward);
        forward = SKMatrix.Concat(SKMatrix.CreateTranslation(ax + offX, ay + offY), forward);

        if (!forward.TryInvert(out SKMatrix inv))
            return null; // collapsed transform (e.g. scale 0): contributes nothing

        var uniforms = new SKRuntimeEffectUniforms(_transform)
        {
            ["m"] = new[] { inv.ScaleX, inv.SkewX, inv.SkewY, inv.ScaleY },
            ["t"] = new[] { inv.TransX, inv.TransY },
            ["opacity"] = opacity,
        };
        var children = new SKRuntimeEffectChildren(_transform) { ["src"] = src };
        return _transform.ToShader(uniforms, children);
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
        foreach (CachedRegisteredEffect cached in _registeredCache.Values)
            cached.Compiled.Dispose();
        _registeredCache.Clear();
        _paint.Dispose();
        _brightness.Dispose();
        _fade.Dispose();
        _color.Dispose();
        _transform.Dispose();
        _crossDissolve.Dispose();
        _dipToBlack.Dispose();
        _dipToWhite.Dispose();
        _wipe.Dispose();
    }
}
