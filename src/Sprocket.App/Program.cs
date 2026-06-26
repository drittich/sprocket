using System;
using System.Linq;
using Avalonia;
using Sprocket.Media;

namespace Sprocket.App;

internal static class Program
{
    /// <summary>
    /// Entry point for the slice's editor app: opens a media file (first CLI arg, or a generated sample
    /// clip), builds a one-video-track project, and plays it in the Skia preview with a transport
    /// (PLAN.md step 4).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Pre-load any FFmpeg natives bundled next to the executable, in dependency order, before
        // anything touches FFmpeg (ARCHITECTURE.md §11). No-op on Windows / local dev.
        FFmpegLoader.EnsureBundledNativesLoaded();

        // Headless release smoke check (scripts/linux-smoke.sh): load the bundled FFmpeg natives and
        // exit, without starting the UI. Proves the per-RID native bundling actually resolves.
        if (args.Contains("--ffmpeg-check"))
            return RunFFmpegCheck();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int RunFFmpegCheck()
    {
        try
        {
            Console.WriteLine($"[ffmpeg-check] OK: {FFmpegDiagnostics.ProbeVersion()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ffmpeg-check] FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
