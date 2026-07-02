using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// Draws a <see cref="ScopeData"/> analysis as a grading scope (PLAN.md step 34): the binned counts are
/// written as trace intensity into one persistent <see cref="SKBitmap"/> (reallocated only when the scope
/// shape changes, so steady-state drawing allocates nothing) which is drawn scaled into the panel, with a
/// graticule stroked over it. Owned and used by one render thread, like <see cref="SkiaEffectPipeline"/>.
/// </summary>
public sealed class ScopeRenderer : IDisposable
{
    private static readonly SKColor Background = new(0x0A, 0x0A, 0x0D);
    private static readonly SKColor Graticule = new(0x3A, 0x3F, 0x4A);

    private SKBitmap? _trace;
    private bool _disposed;

    /// <summary>Renders <paramref name="data"/> into <paramref name="bounds"/> on <paramref name="canvas"/>.</summary>
    public void Draw(SKCanvas canvas, SKRect bounds, ScopeData data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(data);

        canvas.Clear(Background);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        switch (data.Kind)
        {
            case ScopeKind.Waveform when data.Columns > 0:
                DrawWaveTrace(canvas, bounds, data, parade: false);
                DrawLevelGraticule(canvas, bounds);
                break;

            case ScopeKind.RgbParade when data.Columns > 0:
                DrawWaveTrace(canvas, bounds, data, parade: true);
                DrawLevelGraticule(canvas, bounds);
                break;

            case ScopeKind.Vectorscope:
                DrawVectorscope(canvas, bounds, data);
                break;

            case ScopeKind.Histogram:
                DrawHistogram(canvas, bounds, data);
                break;
        }
    }

    // Waveform / parade: one bitmap column per analysis column (×3 sections for the parade), one row per
    // level (level 255 at the top). Count → intensity uses a saturating log-ish curve so single-pixel
    // detail stays visible next to flat areas without blowing out.
    private void DrawWaveTrace(SKCanvas canvas, SKRect bounds, ScopeData data, bool parade)
    {
        int sections = parade ? 3 : 1;
        int w = data.Columns * sections;
        int h = ScopeData.Levels;
        SKBitmap bitmap = EnsureTrace(w, h);

        float norm = NormalisationScale(data);
        unsafe
        {
            var pixels = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            new Span<byte>(pixels, rowBytes * h).Clear();

            for (int c = 0; c < data.Columns; c++)
            {
                for (int level = 0; level < ScopeData.Levels; level++)
                {
                    int y = ScopeData.Levels - 1 - level;
                    if (parade)
                    {
                        WriteTexel(pixels, rowBytes, c, y, Intensity(data.Red[c * ScopeData.Levels + level], norm), 0xFF, 0x60, 0x58);
                        WriteTexel(pixels, rowBytes, data.Columns + c, y, Intensity(data.Green[c * ScopeData.Levels + level], norm), 0x58, 0xE8, 0x6C);
                        WriteTexel(pixels, rowBytes, 2 * data.Columns + c, y, Intensity(data.Blue[c * ScopeData.Levels + level], norm), 0x58, 0x9C, 0xFF);
                    }
                    else
                    {
                        WriteTexel(pixels, rowBytes, c, y, Intensity(data.Luma[c * ScopeData.Levels + level], norm), 0x6C, 0xE8, 0x8C);
                    }
                }
            }
        }
        bitmap.NotifyPixelsChanged();
        canvas.DrawBitmap(bitmap, bounds);

        if (parade)
        {
            using var divider = new SKPaint { Color = Graticule, StrokeWidth = 1 };
            canvas.DrawLine(bounds.Left + bounds.Width / 3f, bounds.Top, bounds.Left + bounds.Width / 3f, bounds.Bottom, divider);
            canvas.DrawLine(bounds.Left + 2f * bounds.Width / 3f, bounds.Top, bounds.Left + 2f * bounds.Width / 3f, bounds.Bottom, divider);
        }
    }

    // Horizontal reference lines at 0 / 25 / 50 / 75 / 100 IRE.
    private static void DrawLevelGraticule(SKCanvas canvas, SKRect bounds)
    {
        using var paint = new SKPaint { Color = Graticule, StrokeWidth = 1 };
        for (int i = 0; i <= 4; i++)
        {
            float y = bounds.Top + bounds.Height * i / 4f;
            canvas.DrawLine(bounds.Left, y, bounds.Right, y, paint);
        }
    }

    private void DrawVectorscope(SKCanvas canvas, SKRect bounds, ScopeData data)
    {
        int n = ScopeData.VectorSize;
        SKBitmap bitmap = EnsureTrace(n, n);

        float norm = NormalisationScale(data);
        unsafe
        {
            var pixels = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            new Span<byte>(pixels, rowBytes * n).Clear();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    WriteTexel(pixels, rowBytes, x, y, Intensity(data.Vector[y * n + x], norm), 0x8C, 0xE8, 0x6C);
        }
        bitmap.NotifyPixelsChanged();

        // The scope keeps a square aspect, centred in the panel.
        float side = Math.Min(bounds.Width, bounds.Height);
        var square = SKRect.Create(bounds.MidX - side / 2f, bounds.MidY - side / 2f, side, side);
        canvas.DrawBitmap(bitmap, square);

        using var paint = new SKPaint { Color = Graticule, StrokeWidth = 1, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle(square.MidX, square.MidY, side / 2f, paint);       // gamut extent
        canvas.DrawCircle(square.MidX, square.MidY, side * 0.375f, paint);   // 75% reference
        canvas.DrawLine(square.MidX, square.Top, square.MidX, square.Bottom, paint);
        canvas.DrawLine(square.Left, square.MidY, square.Right, square.MidY, paint);
    }

    private static void DrawHistogram(SKCanvas canvas, SKRect bounds, ScopeData data)
    {
        if (data.Peak <= 0)
            return;

        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        Span<(int[] bins, SKColor color)> channels =
        [
            (data.Red, new SKColor(0xC8, 0x30, 0x28)),
            (data.Green, new SKColor(0x30, 0xB0, 0x40)),
            (data.Blue, new SKColor(0x30, 0x58, 0xC8)),
        ];

        float binWidth = bounds.Width / ScopeData.Levels;
        foreach ((int[] bins, SKColor color) in channels)
        {
            if (bins.Length != ScopeData.Levels)
                continue;
            paint.Color = color;
            using var path = new SKPath();
            path.MoveTo(bounds.Left, bounds.Bottom);
            for (int i = 0; i < ScopeData.Levels; i++)
            {
                // Square-root scaling keeps sparse levels visible next to dominant ones.
                float hNorm = (float)Math.Sqrt(bins[i] / (double)data.Peak);
                path.LineTo(bounds.Left + i * binWidth, bounds.Bottom - hNorm * bounds.Height);
            }
            path.LineTo(bounds.Right, bounds.Bottom);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    private SKBitmap EnsureTrace(int width, int height)
    {
        if (_trace is null || _trace.Width != width || _trace.Height != height)
        {
            _trace?.Dispose();
            _trace = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        return _trace;
    }

    // Counts normalise against what a fully flat image would put in one bin — a fraction of the analysis
    // height (waveform column) or total sample count (vectorscope) — then saturate.
    private static float NormalisationScale(ScopeData data) => data.Kind switch
    {
        ScopeKind.Vectorscope => Math.Max(1f, data.Peak * 0.25f),
        _ => Math.Max(1f, data.Peak * 0.5f),
    };

    private static byte Intensity(int count, float norm) =>
        count <= 0 ? (byte)0 : (byte)Math.Clamp(30.0 + 225.0 * Math.Min(1.0, count / norm), 0, 255);

    private static unsafe void WriteTexel(byte* pixels, int rowBytes, int x, int y, byte intensity, byte r, byte g, byte b)
    {
        if (intensity == 0)
            return;
        byte* p = pixels + y * rowBytes + x * 4;
        // Premultiplied: the trace colour scaled by intensity, opaque against the cleared background.
        p[0] = (byte)(r * intensity / 255);
        p[1] = (byte)(g * intensity / 255);
        p[2] = (byte)(b * intensity / 255);
        p[3] = intensity;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _trace?.Dispose();
        _trace = null;
    }
}
