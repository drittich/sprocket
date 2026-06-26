using System;
using System.Diagnostics;
using System.IO;

namespace Sprocket.Spike;

/// <summary>
/// Ensures a 1080p test clip exists. Pre-generated into assets/ at build time and copied
/// to the output dir; falls back to generating one with the ffmpeg CLI if missing.
/// </summary>
internal static class TestAsset
{
    public static string EnsureExists()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets");
        string path = Path.Combine(dir, "test.mp4");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=2 -pix_fmt yuv420p \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process? p = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg CLI not found to generate the test asset.");
        p.WaitForExit();

        if (!File.Exists(path))
            throw new InvalidOperationException("Failed to generate the test asset via ffmpeg.");
        return path;
    }
}
