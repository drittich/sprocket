using System.Diagnostics;
using System.Runtime.InteropServices;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Unit coverage for <see cref="MediaEncoder"/> input validation. The 4:2:0 H.264 encoder requires even output
/// dimensions; libx264 rejects odd sizes at open and (via Sdcb) a failed open crashed the process during cleanup,
/// so <see cref="MediaEncoder.Create"/> rejects odd dimensions up front with a clear managed exception instead.
/// </summary>
public class MediaEncoderTests
{
    private static readonly Rational Fps = new(30, 1);

    [Theory]
    [InlineData(1921, 1080)] // odd width
    [InlineData(1920, 1081)] // odd height
    [InlineData(1281, 721)]  // both odd
    public void Create_RejectsOddDimensions_WithoutCrashing(int width, int height)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-enc-{System.Guid.NewGuid():N}.mp4");
        try
        {
            Assert.Throws<System.ArgumentException>(() =>
                MediaEncoder.Create(path, new VideoEncoderSettings(width, height, Fps)));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Exported_Mp4_CarriesSprocketCreationMetadata()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-meta-{System.Guid.NewGuid():N}.mp4");
        try
        {
            // Encode a couple of solid RGBA frames — enough to produce a valid, probe-able MP4.
            const int w = 64, h = 48, rowBytes = w * 4;
            var rgba = new byte[rowBytes * h];
            using (MediaEncoder encoder = MediaEncoder.Create(path, new VideoEncoderSettings(w, h, Fps)))
            {
                GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                try
                {
                    nint pixels = pin.AddrOfPinnedObject();
                    for (long i = 0; i < 3; i++)
                        encoder.WriteVideoFrame(pixels, rowBytes, i);
                }
                finally { pin.Free(); }
                encoder.Finish();
            }

            string tags = ProbeFormatTags(path);
            Assert.Contains("Created with Sprocket", tags);
            Assert.Contains("Sprocket", tags); // the `encoder` tag, e.g. "Sprocket 0.1.27"
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>Reads the container-level metadata tags from <paramref name="path"/> with the <c>ffprobe</c> CLI
    /// (alongside <c>ffmpeg</c> on PATH, per the test prerequisites).</summary>
    private static string ProbeFormatTags(string path)
    {
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -show_entries format_tags -of default=noprint_wrappers=1 \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffprobe CLI not found on PATH.");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }
}
