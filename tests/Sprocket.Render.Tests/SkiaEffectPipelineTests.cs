using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Exercises the real SkSL brightness/fade shaders on an offscreen raster surface (no GPU/GUI), reading
/// pixels back to assert the effect math. This is the headless counterpart of the spike's Linux check:
/// SkiaSharp runs the runtime effects on the CPU backend, so the same shader code that runs on the GPU in
/// the preview is validated here deterministically (PLAN.md step 7).
/// </summary>
public sealed class SkiaEffectPipelineTests
{
    private const int Size = 8;
    private static readonly byte Gray = 100; // mid-grey source so brightness up/down both stay in range

    [Fact]
    public void NoEffects_DrawsSourceUnchanged()
    {
        byte v = RenderCenter([]);
        Assert.InRange(v, Gray - 2, Gray + 2);
    }

    [Fact]
    public void Brightness_AboveOne_Brightens()
    {
        byte v = RenderCenter([Brightness(1.6)]);
        Assert.InRange(v, (int)(Gray * 1.6) - 3, (int)(Gray * 1.6) + 3);
    }

    [Fact]
    public void Brightness_BelowOne_Darkens()
    {
        byte v = RenderCenter([Brightness(0.5)]);
        Assert.InRange(v, (int)(Gray * 0.5) - 3, (int)(Gray * 0.5) + 3);
    }

    [Fact]
    public void Fade_HalfOpacity_HalvesTowardBlackBackground()
    {
        byte v = RenderCenter([Fade(0.5)]);
        Assert.InRange(v, (int)(Gray * 0.5) - 3, (int)(Gray * 0.5) + 3);
    }

    [Fact]
    public void Fade_ZeroOpacity_IsBackground()
    {
        byte v = RenderCenter([Fade(0.0)]);
        Assert.InRange(v, 0, 2);
    }

    [Fact]
    public void Chain_BrightnessThenFade_AppliesBoth()
    {
        // 100 × 2.0 brightness = 200, then × 0.25 fade over black = 50.
        byte v = RenderCenter([Brightness(2.0), Fade(0.25)]);
        Assert.InRange(v, 50 - 4, 50 + 4);
    }

    [Fact]
    public void UnknownEffect_IsPassedThrough()
    {
        var unknown = new ResolvedEffect("plugin.does.not.exist", new Dictionary<string, double>());
        byte v = RenderCenter([unknown]);
        Assert.InRange(v, Gray - 2, Gray + 2);
    }

    [Fact]
    public void DegenerateBounds_DoNotThrow()
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var src = MakeSource();
        pipeline.Present(surface.Canvas, SKRect.Create(0, 0), src.GetPixels(), src.RowBytes, Size, Size, [Brightness(2.0)], SKColors.Black);
        // No assertion beyond "did not throw"; an empty target rect is a no-op after the clear.
    }

    // ── Step 16: Color (exposure/contrast/saturation) ────────────────────────────────────────────────

    [Fact]
    public void Color_ExposureUp_OneStop_Doubles()
    {
        // 100 × 2^1 = 200.
        byte v = RenderCenter([Color(exposure: 1.0)]);
        Assert.InRange(v, 200 - 4, 200 + 4);
    }

    [Fact]
    public void Color_ExposureDown_OneStop_Halves()
    {
        byte v = RenderCenter([Color(exposure: -1.0)]);
        Assert.InRange(v, 50 - 4, 50 + 4);
    }

    [Fact]
    public void Color_Contrast_PushesBelowMidGrey_Darker()
    {
        // Gray 100/255 = 0.392, mid 0.5, contrast 2.0 → (0.392-0.5)*2+0.5 = 0.284 → ~72.
        byte v = RenderCenter([Color(contrast: 2.0)]);
        Assert.InRange(v, 72 - 5, 72 + 5);
    }

    [Fact]
    public void Color_Identity_LeavesSourceUnchanged()
    {
        byte v = RenderCenter([Color()]);
        Assert.InRange(v, Gray - 2, Gray + 2);
    }

    // ── Step 16: Transform (geometry + opacity) ───────────────────────────────────────────────────────

    [Fact]
    public void Transform_Identity_LeavesCentreUnchanged()
    {
        byte v = RenderCenter([Transform()]);
        Assert.InRange(v, Gray - 2, Gray + 2);
    }

    [Fact]
    public void Transform_Opacity_HalvesTowardBackground()
    {
        byte v = RenderCenter([Transform(opacity: 0.5)]);
        Assert.InRange(v, (int)(Gray * 0.5) - 4, (int)(Gray * 0.5) + 4);
    }

    [Fact]
    public void Transform_PositionShift_MovesContentOffCentre_RevealingBackground()
    {
        // Shift a full frame width to the right: the centre samples outside the frame → Decal transparent →
        // black background shows through.
        byte v = RenderCenter([Transform(positionX: 1.0)]);
        Assert.InRange(v, 0, 3);
    }

    [Fact]
    public void Transform_ThenColor_Chains()
    {
        // Transform identity (gray 100 stays), then exposure +1 → ~200. Proves geometry + colour compose.
        byte v = RenderCenter([Transform(), Color(exposure: 1.0)]);
        Assert.InRange(v, 200 - 5, 200 + 5);
    }

    // ── Step 19: Generators ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generator_SolidColor_FillsFrame()
    {
        // #FF3366CC → R=0x33(51), G=0x66(102), B=0xCC(204).
        SKColor c = RenderGeneratorPixel(SolidColor("#FF3366CC"), 16, 8, 8);
        Assert.InRange(c.Red, 51 - 2, 51 + 2);
        Assert.InRange(c.Green, 102 - 2, 102 + 2);
        Assert.InRange(c.Blue, 204 - 2, 204 + 2);
    }

    [Fact]
    public void Generator_Title_DrawsTextOverBackground()
    {
        var gen = new ResolvedGenerator(
            GeneratorTypeIds.Title,
            new Dictionary<string, string>
            {
                [GeneratorParamNames.Text] = "X",
                [GeneratorParamNames.Color] = "#FFFFFFFF",        // white text
                [GeneratorParamNames.BackgroundColor] = "#FF101010", // near-black bg (R=16)
            },
            new Dictionary<string, double> { [GeneratorParamNames.FontSize] = 0.7 });

        // A corner is background; the centre is covered by the large white glyph.
        Assert.InRange(RenderGeneratorPixel(gen, 64, 2, 2).Red, 16 - 3, 16 + 3);
        Assert.True(RenderGeneratorPixel(gen, 64, 32, 32).Red > 180, "Title glyph should brighten the centre.");
    }

    [Fact]
    public void Generator_UnknownType_DrawsNothing()
    {
        var gen = new ResolvedGenerator("plugin.unknown.gen", new Dictionary<string, string>(), new Dictionary<string, double>());
        // Over a black surface the unknown generator leaves it black (transparent over black).
        Assert.InRange(RenderGeneratorPixel(gen, 16, 8, 8).Red, 0, 3);
    }

    // ── Step 19: Adjustment layers ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Adjustment_AppliesEffectToCompositeBeneath()
    {
        // A gray base layer, then an adjustment with brightness 0.5 → centre halved (the adjustment regrades it).
        byte v = RenderAdjustmentCenter([Brightness(0.5)]);
        Assert.InRange(v, (int)(Gray * 0.5) - 3, (int)(Gray * 0.5) + 3);
    }

    [Fact]
    public void Adjustment_NoEffects_IsNoOp()
    {
        byte v = RenderAdjustmentCenter([]);
        Assert.InRange(v, Gray - 2, Gray + 2);
    }

    private static ResolvedGenerator SolidColor(string colorHex) =>
        new(GeneratorTypeIds.SolidColor,
            new Dictionary<string, string> { [GeneratorParamNames.Color] = colorHex },
            new Dictionary<string, double>());

    private static SKColor RenderGeneratorPixel(ResolvedGenerator generator, int size, int px, int py)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        pipeline.DrawGenerator(surface.Canvas, SKRect.Create(size, size), generator, size, size, []);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(px, py);
    }

    private static byte RenderAdjustmentCenter(IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        using var src = MakeSource();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(Size, Size), src.GetPixels(), src.RowBytes, Size, Size, []);
        pipeline.DrawAdjustment(surface, SKRect.Create(Size, Size), effects);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(Size / 2, Size / 2).Red;
    }

    private static ResolvedEffect Brightness(double amount) =>
        new(EffectTypeIds.Brightness, new Dictionary<string, double> { [EffectParamNames.Amount] = amount });

    private static ResolvedEffect Fade(double opacity) =>
        new(EffectTypeIds.Fade, new Dictionary<string, double> { [EffectParamNames.Opacity] = opacity });

    private static ResolvedEffect Color(double exposure = 0.0, double contrast = 1.0, double saturation = 1.0) =>
        new(EffectTypeIds.Color, new Dictionary<string, double>
        {
            [EffectParamNames.Exposure] = exposure,
            [EffectParamNames.Contrast] = contrast,
            [EffectParamNames.Saturation] = saturation,
        });

    private static ResolvedEffect Transform(
        double scale = 1.0, double positionX = 0.0, double positionY = 0.0,
        double rotation = 0.0, double anchorX = 0.5, double anchorY = 0.5, double opacity = 1.0) =>
        new(EffectTypeIds.Transform, new Dictionary<string, double>
        {
            [EffectParamNames.Scale] = scale,
            [EffectParamNames.PositionX] = positionX,
            [EffectParamNames.PositionY] = positionY,
            [EffectParamNames.Rotation] = rotation,
            [EffectParamNames.AnchorX] = anchorX,
            [EffectParamNames.AnchorY] = anchorY,
            [EffectParamNames.Opacity] = opacity,
        });

    private static SKBitmap MakeSource()
    {
        var bmp = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(Gray, Gray, Gray, 255));
        return bmp;
    }

    /// <summary>Renders the source frame with the given effect chain onto a black raster surface the same size
    /// as the frame (so the fit rect is 1:1, no scaling) and returns the centre pixel's red channel.</summary>
    private static byte RenderCenter(IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        using var src = MakeSource();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));

        pipeline.Present(
            surface.Canvas, SKRect.Create(Size, Size),
            src.GetPixels(), src.RowBytes, Size, Size, effects, SKColors.Black);
        surface.Canvas.Flush();

        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(Size / 2, Size / 2).Red;
    }
}
