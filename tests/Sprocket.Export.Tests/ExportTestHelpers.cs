using Sprocket.Media;

namespace Sprocket.Export.Tests;

/// <summary>A scratch output path (default <c>.mp4</c>) that deletes itself on dispose.</summary>
internal sealed class TempFile(string extension = ".mp4") : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-export-{Guid.NewGuid():N}{extension}");

    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>Decode-side assertions shared by the export tests: reopen a written file and count frames / measure
/// the first frame's brightness. Software decode, so it is deterministic across machines.</summary>
internal static class ExportProbe
{
    public static int CountVideoFrames(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        int count = 0;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
            using (frame)
                count++;
        return count;
    }

    /// <summary>Mean of the R/G/B channels of the first decoded frame (0–255), as a brightness proxy.</summary>
    public static double FirstFrameMeanRgb(string path) => FirstFrameRegionMeanRgb(path, 0, 0, 1, 1);

    /// <summary>Mean R/G/B (0–255) of a fractional sub-rectangle of the first decoded frame — the corners are
    /// given as fractions in [0, 1]. Used to check a burn-in changed one region of the frame far more than the
    /// rest (localisation), independent of the platform font's exact glyphs.</summary>
    public static unsafe double FirstFrameRegionMeanRgb(string path, double x0, double y0, double x1, double y1)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        if (!source.TryDecodeNextFrame(pool, out VideoFrame? frame))
            throw new InvalidOperationException($"No decodable frame in {path}.");
        using (frame)
        {
            int xa = Math.Clamp((int)(x0 * frame!.Width), 0, frame.Width);
            int xb = Math.Clamp((int)(x1 * frame.Width), xa + 1, frame.Width);
            int ya = Math.Clamp((int)(y0 * frame.Height), 0, frame.Height);
            int yb = Math.Clamp((int)(y1 * frame.Height), ya + 1, frame.Height);

            var p = (byte*)frame.Pixels;
            int rowBytes = frame.RowBytes;
            long sum = 0;
            long count = 0;
            for (int y = ya; y < yb; y++)
            {
                byte* row = p + (long)y * rowBytes;
                for (int x = xa; x < xb; x++)
                {
                    byte* px = row + x * 4; // RGBA
                    sum += px[0] + px[1] + px[2];
                    count += 3;
                }
            }
            return count == 0 ? 0 : (double)sum / count;
        }
    }
}
