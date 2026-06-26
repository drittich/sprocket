using System;
using Avalonia;

namespace Sprocket.Spike;

internal static class Program
{
    // Architecture spike entry point. Proves the load-bearing claims from ARCHITECTURE.md:
    //   FFmpeg decode (Sdcb.FFmpeg) -> native RGBA buffer -> SKImage ->
    //   brightness SKRuntimeEffect (GPU) -> Avalonia preview via shared GRContext,
    // with the render loop measured for per-frame managed allocation.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless cross-platform smoke test (no GUI) — used for Linux verification.
        if (Array.IndexOf(args, "--headless-check") >= 0)
            return HeadlessCheck.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
