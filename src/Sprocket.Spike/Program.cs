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
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
