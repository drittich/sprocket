using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The grading-scope analysis (PLAN.md step 34): <see cref="ScopeAnalyzer"/> binning synthetic RGBA
/// buffers into waveform / parade / vectorscope / histogram counts. Pure count math, canvas-free —
/// the drawing (<see cref="ScopeRenderer"/>) rests on manual verification, per the
/// <c>MonitorOverlayTests</c> convention.
/// </summary>
public sealed class ScopeAnalyzerTests
{
    private const int Width = 16;
    private const int Height = 12;

    [Fact]
    public void Histogram_SolidGray_BinsEveryPixelAtItsLevel()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Histogram, Solid(128, 128, 128), Width, Height, Width * 4, data);

        Assert.Equal(Width * Height, data.Luma[128]);
        Assert.Equal(Width * Height, data.Red[128]);
        Assert.Equal(Width * Height, data.Green[128]);
        Assert.Equal(Width * Height, data.Blue[128]);
        Assert.Equal(Width * Height, data.Peak);
        Assert.Equal(0, data.Luma[127]); // nothing lands elsewhere
    }

    [Fact]
    public void Histogram_MixedColor_UsesPerChannelLevels()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Histogram, Solid(200, 100, 50), Width, Height, Width * 4, data);

        Assert.Equal(Width * Height, data.Red[200]);
        Assert.Equal(Width * Height, data.Green[100]);
        Assert.Equal(Width * Height, data.Blue[50]);
        Assert.Equal(Width * Height, data.Luma[ScopeAnalyzer.Luma709(200, 100, 50)]);
    }

    [Fact]
    public void Waveform_SolidGray_PutsEveryColumnAtTheLumaLevel()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Waveform, Solid(128, 128, 128), Width, Height, Width * 4, data);

        Assert.Equal(Width, data.Columns);
        for (int c = 0; c < Width; c++)
            Assert.Equal(Height, data.Luma[c * ScopeData.Levels + 128]);
    }

    [Fact]
    public void Waveform_HalfBlackHalfWhite_SplitsEachColumn()
    {
        // Top half white, bottom half black — every column shows both levels, half the rows each.
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                byte v = y < Height / 2 ? (byte)255 : (byte)0;
                int i = (y * Width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }

        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Waveform, pixels, Width, Height, Width * 4, data);

        for (int c = 0; c < Width; c++)
        {
            Assert.Equal(Height / 2, data.Luma[c * ScopeData.Levels + 255]);
            Assert.Equal(Height / 2, data.Luma[c * ScopeData.Levels + 0]);
        }
    }

    [Fact]
    public void Parade_BinsEachChannelPerColumn()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.RgbParade, Solid(200, 100, 50), Width, Height, Width * 4, data);

        for (int c = 0; c < Width; c++)
        {
            Assert.Equal(Height, data.Red[c * ScopeData.Levels + 200]);
            Assert.Equal(Height, data.Green[c * ScopeData.Levels + 100]);
            Assert.Equal(Height, data.Blue[c * ScopeData.Levels + 50]);
        }
    }

    [Fact]
    public void Vectorscope_Neutral_LandsAtTheCenter()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Vectorscope, Solid(128, 128, 128), Width, Height, Width * 4, data);

        int center = ScopeData.VectorSize / 2 - 1; // Cb = Cr = 0 → bin 63 of 128
        Assert.Equal(Width * Height, data.Vector[center * ScopeData.VectorSize + center]);
    }

    [Fact]
    public void Vectorscope_PureRed_LandsUpAndLeftOfCenter()
    {
        // Red: Cr > 0 (up = lower row index), Cb < 0 (left = lower column index).
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Vectorscope, Solid(255, 0, 0), Width, Height, Width * 4, data);

        int hot = Array.IndexOf(data.Vector, data.Peak);
        int row = hot / ScopeData.VectorSize;
        int col = hot % ScopeData.VectorSize;
        Assert.True(row < ScopeData.VectorSize / 2 - 4, $"red should plot above centre (row {row})");
        Assert.True(col < ScopeData.VectorSize / 2 - 4, $"red should plot left of centre (col {col})");
        Assert.Equal(Width * Height, data.Peak);
    }

    [Fact]
    public void Analyze_ReusesAndClearsTheBins()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Histogram, Solid(128, 128, 128), Width, Height, Width * 4, data);
        int[] first = data.Luma;
        ScopeAnalyzer.Analyze(ScopeKind.Histogram, Solid(64, 64, 64), Width, Height, Width * 4, data);

        Assert.Same(first, data.Luma);            // the array is reused, not reallocated
        Assert.Equal(0, data.Luma[128]);          // …and cleared between frames
        Assert.Equal(Width * Height, data.Luma[64]);
    }

    [Fact]
    public void Analyze_RespectsRowStridePadding()
    {
        // Rows padded to 4 extra pixels of garbage — the analysis must only read the leading Width pixels.
        int rowBytes = (Width + 4) * 4;
        byte[] pixels = new byte[rowBytes * Height];
        Array.Fill(pixels, (byte)255); // garbage everywhere…
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * rowBytes + x * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 64;
                pixels[i + 3] = 255;
            }

        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.Histogram, pixels, Width, Height, rowBytes, data);
        Assert.Equal(Width * Height, data.Luma[64]);
        Assert.Equal(0, data.Luma[255]);
    }

    [Fact]
    public void Analyze_None_LeavesNoActiveBins()
    {
        var data = new ScopeData();
        ScopeAnalyzer.Analyze(ScopeKind.None, Solid(128, 128, 128), Width, Height, Width * 4, data);
        Assert.Equal(ScopeKind.None, data.Kind);
        Assert.Equal(0, data.Peak);
    }

    private static byte[] Solid(byte r, byte g, byte b)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
