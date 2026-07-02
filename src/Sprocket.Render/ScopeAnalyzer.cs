namespace Sprocket.Render;

/// <summary>Which grading scope the monitor's scope panel shows (PLAN.md step 34).</summary>
public enum ScopeKind
{
    /// <summary>Scopes hidden.</summary>
    None,

    /// <summary>Luma waveform: signal level (vertical) per image column (horizontal).</summary>
    Waveform,

    /// <summary>RGB parade: three side-by-side per-channel waveforms.</summary>
    RgbParade,

    /// <summary>Vectorscope: chroma (Cb/Cr) distribution around neutral.</summary>
    Vectorscope,

    /// <summary>Per-channel + luma level histogram.</summary>
    Histogram,
}

/// <summary>
/// One scope's binned analysis of a rendered frame — reused across frames so steady-state analysis
/// allocates nothing (§1's spirit: the arrays are counts, never pixels). Written by
/// <see cref="ScopeAnalyzer.Analyze"/> and read by <see cref="ScopeRenderer"/>; producer and consumer
/// both run on the render thread, so no synchronisation is needed.
/// </summary>
public sealed class ScopeData
{
    /// <summary>Signal levels the waveform/parade/histogram bin over (8-bit).</summary>
    public const int Levels = 256;

    /// <summary>The vectorscope's square bin grid size per axis.</summary>
    public const int VectorSize = 128;

    /// <summary>The scope the current bins were computed for.</summary>
    public ScopeKind Kind { get; internal set; }

    /// <summary>Horizontal bin count of the waveform / parade (the analysis width).</summary>
    public int Columns { get; internal set; }

    /// <summary>Waveform luma counts, <see cref="Columns"/> × <see cref="Levels"/> (column-major:
    /// <c>[column * Levels + level]</c>). Also the luma histogram (length <see cref="Levels"/>) for
    /// <see cref="ScopeKind.Histogram"/>.</summary>
    public int[] Luma { get; internal set; } = [];

    /// <summary>Red channel counts: parade columns × levels, or histogram levels.</summary>
    public int[] Red { get; internal set; } = [];

    /// <summary>Green channel counts: parade columns × levels, or histogram levels.</summary>
    public int[] Green { get; internal set; } = [];

    /// <summary>Blue channel counts: parade columns × levels, or histogram levels.</summary>
    public int[] Blue { get; internal set; } = [];

    /// <summary>Vectorscope Cb×Cr counts, <see cref="VectorSize"/>² (<c>[row * VectorSize + column]</c>,
    /// row 0 = maximum Cr, i.e. the top of the display).</summary>
    public int[] Vector { get; internal set; } = [];

    /// <summary>The largest single bin count, for display normalisation.</summary>
    public int Peak { get; internal set; }

    internal static int[] Ensure(int[] array, int length)
    {
        if (array.Length != length)
            return new int[length];
        Array.Clear(array);
        return array;
    }
}

/// <summary>
/// Bins a rendered RGBA frame into grading-scope data (PLAN.md step 34): luma waveform, RGB parade,
/// vectorscope, and histogram. Pure CPU count math over a caller-supplied (already downscaled) pixel
/// buffer into caller-owned reusable arrays — no Skia, no allocation in steady state — so it is
/// unit-testable with synthetic pixels, following the <c>MonitorOverlay</c> convention of keeping the
/// computation canvas-free.
/// </summary>
public static class ScopeAnalyzer
{
    /// <summary>
    /// Analyzes <paramref name="rgba"/> (RGBA8888, premultiplied or opaque; alpha is ignored — the
    /// monitor composite is opaque over its background) of <paramref name="width"/>×<paramref name="height"/>
    /// with stride <paramref name="rowBytes"/> into <paramref name="data"/> for <paramref name="kind"/>.
    /// The arrays in <paramref name="data"/> are resized once and reused thereafter.
    /// </summary>
    public static void Analyze(ScopeKind kind, ReadOnlySpan<byte> rgba, int width, int height, int rowBytes, ScopeData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Kind = kind;
        data.Peak = 0;
        if (kind == ScopeKind.None || width <= 0 || height <= 0 || rowBytes < width * 4)
            return;

        switch (kind)
        {
            case ScopeKind.Waveform:
                data.Columns = width;
                data.Luma = ScopeData.Ensure(data.Luma, width * ScopeData.Levels);
                break;
            case ScopeKind.RgbParade:
                data.Columns = width;
                data.Red = ScopeData.Ensure(data.Red, width * ScopeData.Levels);
                data.Green = ScopeData.Ensure(data.Green, width * ScopeData.Levels);
                data.Blue = ScopeData.Ensure(data.Blue, width * ScopeData.Levels);
                break;
            case ScopeKind.Vectorscope:
                data.Vector = ScopeData.Ensure(data.Vector, ScopeData.VectorSize * ScopeData.VectorSize);
                break;
            case ScopeKind.Histogram:
                data.Luma = ScopeData.Ensure(data.Luma, ScopeData.Levels);
                data.Red = ScopeData.Ensure(data.Red, ScopeData.Levels);
                data.Green = ScopeData.Ensure(data.Green, ScopeData.Levels);
                data.Blue = ScopeData.Ensure(data.Blue, ScopeData.Levels);
                break;
        }

        int peak = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * rowBytes, width * 4);
            for (int x = 0; x < width; x++)
            {
                byte r = row[x * 4];
                byte g = row[x * 4 + 1];
                byte b = row[x * 4 + 2];

                switch (kind)
                {
                    case ScopeKind.Waveform:
                    {
                        int level = Luma709(r, g, b);
                        peak = Math.Max(peak, ++data.Luma[x * ScopeData.Levels + level]);
                        break;
                    }
                    case ScopeKind.RgbParade:
                    {
                        int baseIdx = x * ScopeData.Levels;
                        peak = Math.Max(peak, ++data.Red[baseIdx + r]);
                        peak = Math.Max(peak, ++data.Green[baseIdx + g]);
                        peak = Math.Max(peak, ++data.Blue[baseIdx + b]);
                        break;
                    }
                    case ScopeKind.Vectorscope:
                    {
                        // BT.709 chroma, each in [-0.5, 0.5]; row 0 = +Cr (top of the display).
                        double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                        double cb = (b / 255.0 - luma) / 1.8556;
                        double cr = (r / 255.0 - luma) / 1.5748;
                        int ix = (int)Math.Clamp((cb + 0.5) * (ScopeData.VectorSize - 1), 0, ScopeData.VectorSize - 1);
                        int iy = (int)Math.Clamp((0.5 - cr) * (ScopeData.VectorSize - 1), 0, ScopeData.VectorSize - 1);
                        peak = Math.Max(peak, ++data.Vector[iy * ScopeData.VectorSize + ix]);
                        break;
                    }
                    case ScopeKind.Histogram:
                    {
                        peak = Math.Max(peak, ++data.Luma[Luma709(r, g, b)]);
                        peak = Math.Max(peak, ++data.Red[r]);
                        peak = Math.Max(peak, ++data.Green[g]);
                        peak = Math.Max(peak, ++data.Blue[b]);
                        break;
                    }
                }
            }
        }
        data.Peak = peak;
    }

    /// <summary>Rec.709 luma of 8-bit RGB, as an 8-bit level.</summary>
    public static int Luma709(byte r, byte g, byte b) =>
        Math.Clamp((int)(0.2126 * r + 0.7152 * g + 0.0722 * b + 0.5), 0, 255);
}
