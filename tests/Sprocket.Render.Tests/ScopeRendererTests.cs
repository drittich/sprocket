using SkiaSharp;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Smoke coverage for <see cref="ScopeRenderer"/> (PLAN.md step 34): each scope draws its analysed
/// bins into an offscreen raster surface without faulting and produces visible trace pixels over the
/// scope background. Exact trace appearance rests on manual verification (the
/// <c>MonitorOverlayTests</c> convention); this pins the plumbing — bitmap sizing, the unsafe texel
/// writes, and graticule drawing.
/// </summary>
public sealed class ScopeRendererTests
{
    private const int Width = 32;
    private const int Height = 24;

    [Theory]
    [InlineData(ScopeKind.Waveform)]
    [InlineData(ScopeKind.RgbParade)]
    [InlineData(ScopeKind.Vectorscope)]
    [InlineData(ScopeKind.Histogram)]
    public void Draw_ProducesTracePixels(ScopeKind kind)
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(kind, Gradient(), Width, Height, Width * 4, data);

        using var renderer = new ScopeRenderer();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(240, 160, SKColorType.Rgba8888, SKAlphaType.Premul));
        renderer.Draw(surface.Canvas, SKRect.Create(240, 160), data);
        surface.Canvas.Flush();

        using SKPixmap pixels = surface.PeekPixels();
        int lit = 0;
        for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
            {
                SKColor c = pixels.GetPixelColor(x, y);
                if (c.Red > 40 || c.Green > 40 || c.Blue > 40)
                    lit++;
            }
        Assert.True(lit > 20, $"{kind} should draw visible trace/graticule pixels (lit {lit})");
    }

    [Fact]
    public void Draw_EmptyData_DrawsOnlyTheBackground()
    {
        using var renderer = new ScopeRenderer();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
        renderer.Draw(surface.Canvas, SKRect.Create(64, 48), new ScopeData()); // Kind = None, no bins
        surface.Canvas.Flush();

        using SKPixmap pixels = surface.PeekPixels();
        SKColor c = pixels.GetPixelColor(32, 24);
        Assert.True(c.Red < 20 && c.Green < 20 && c.Blue < 20, $"None should leave the dark background ({c})");
    }

    // A horizontal hue-ish gradient with a vertical brightness ramp: spreads counts across columns,
    // levels, and chroma bins so every scope has something to trace.
    private static byte[] Gradient()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                pixels[i] = (byte)(x * 255 / (Width - 1));
                pixels[i + 1] = (byte)(y * 255 / (Height - 1));
                pixels[i + 2] = (byte)(255 - x * 255 / (Width - 1));
                pixels[i + 3] = 255;
            }
        return pixels;
    }
}
