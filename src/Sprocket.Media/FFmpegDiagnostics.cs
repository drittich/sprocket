using Sdcb.FFmpeg.Raw;

namespace Sprocket.Media;

/// <summary>
/// Minimal FFmpeg load probe. Touching any <c>ffmpeg.*</c> entry point forces the native FFmpeg
/// libraries to resolve through the OS loader from the application directory — the no-<c>RootPath</c>
/// bundling path a shipped build depends on (ARCHITECTURE.md §11). The headless smoke check
/// (<c>scripts/linux-smoke.sh</c>) calls this against a published bundle to prove the bundled
/// <c>.so</c>/<c>.dylib</c>/<c>.dll</c> files actually load; it throws <see cref="System.DllNotFoundException"/>
/// if the natives are missing or unresolved.
/// </summary>
public static class FFmpegDiagnostics
{
    /// <summary>
    /// Loads the core FFmpeg libraries and returns a one-line version summary. Calling each library's
    /// <c>*_version()</c> pulls that specific shared object in, so a successful return proves every core
    /// lib (avutil, avcodec, avformat, swscale, swresample) resolved — not just one.
    /// </summary>
    public static string ProbeVersion()
    {
        FFmpegLoader.EnsureBundledNativesLoaded();

        static string V(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        return string.Join(", ",
            $"avutil {V(ffmpeg.avutil_version())}",
            $"avcodec {V(ffmpeg.avcodec_version())}",
            $"avformat {V(ffmpeg.avformat_version())}",
            $"swscale {V(ffmpeg.swscale_version())}",
            $"swresample {V(ffmpeg.swresample_version())}");
    }
}
