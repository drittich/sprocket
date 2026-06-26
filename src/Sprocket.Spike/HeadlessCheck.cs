using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Sprocket.Spike;

/// <summary>
/// Headless cross-platform smoke test (no GUI / no GPU display required). Exercises the
/// load-bearing media + render path on the current OS:
///   Sdcb.FFmpeg decode -> native RGBA -> SKImage -> brightness SKRuntimeEffect (SkSL)
///   -> offscreen SKSurface -> PNG.
/// Used to verify the stack runs on Linux, where decode relies on bundled FFmpeg 7 libs
/// resolved via LD_LIBRARY_PATH (there is no Sdcb Linux runtime NuGet).
/// Run with: <c>dotnet Sprocket.Spike.dll --headless-check</c>
/// </summary>
internal static class HeadlessCheck
{
    private const string BrightnessSksl = @"
uniform shader src;
uniform float amount;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    return half4(c.rgb * amount, c.a);
}";

    public static int Run()
    {
        Console.WriteLine($"[headless] OS={RuntimeInformation.OSDescription.Trim()}  arch={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"[headless] LD_LIBRARY_PATH={Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")}");

        try
        {
            string path = TestAsset.EnsureExists();

            using DecodedFrame frame = DecodedFrame.DecodeFirst(path);
            Console.WriteLine($"[headless] DECODE OK via Sdcb.FFmpeg: {frame.Width}x{frame.Height} (native RGBA, stride {frame.RowBytes})");

            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using SKImage src = SKImage.FromPixels(info, frame.Pixels, frame.RowBytes);

            using SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(BrightnessSksl, out string errors)
                ?? throw new InvalidOperationException($"SkSL compile failed: {errors}");

            const float amount = 1.30f;

            using SKSurface surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException("Failed to create offscreen SKSurface.");
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using SKShader imgShader = src.ToShader(
                SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            var uniforms = new SKRuntimeEffectUniforms(effect) { ["amount"] = amount };
            var children = new SKRuntimeEffectChildren(effect) { ["src"] = imgShader };
            using SKShader shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(SKRect.Create(0, 0, frame.Width, frame.Height), paint);
            Console.WriteLine($"[headless] SHADER OK: brightness SKRuntimeEffect applied (amount x{amount})");

            double srcMean = MeanRed(src.PeekPixels());
            double outMean = MeanRed(surface.PeekPixels());
            Console.WriteLine($"[headless] pixel check: mean R  src={srcMean:0.0}  out={outMean:0.0}  (out should be brighter)");

            using SKImage outImg = surface.Snapshot();
            using SKData png = outImg.Encode(SKEncodedImageFormat.Png, 90);
            string outPath = Path.Combine(AppContext.BaseDirectory, "headless-out.png");
            File.WriteAllBytes(outPath, png.ToArray());
            Console.WriteLine($"[headless] ENCODE OK: wrote {outPath} ({png.Size} bytes)");

            bool brighter = outMean >= srcMean;
            Console.WriteLine(brighter
                ? "[headless] RESULT: PASS — decode + SkSL shader + Skia render all work on this OS."
                : "[headless] RESULT: FAIL — output not brighter than source.");
            return brighter ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[headless] RESULT: FAIL — {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static unsafe double MeanRed(SKPixmap pm)
    {
        byte* p = (byte*)pm.GetPixels();
        int w = pm.Width, h = pm.Height, rowBytes = pm.RowBytes;
        long sum = 0;
        int count = 0;
        for (int y = 0; y < h; y += 17)
        for (int x = 0; x < w; x += 17)
        {
            sum += p[y * rowBytes + x * 4]; // R channel of Rgba8888
            count++;
        }
        return count > 0 ? (double)sum / count : 0;
    }
}
