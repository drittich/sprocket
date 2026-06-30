using System.Runtime.InteropServices;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// FFmpeg load probe + version reporting. Touching any binding entry point forces the native FFmpeg
/// libraries to resolve through <see cref="FFmpegLoader"/> from the application directory — the bundling
/// path a shipped build depends on (ARCHITECTURE.md §11). The headless smoke check
/// (<c>scripts/linux-smoke.sh</c>) calls <see cref="ProbeVersion"/> against a published bundle to prove
/// the bundled <c>.so</c>/<c>.dylib</c>/<c>.dll</c> files actually load; it throws
/// <see cref="System.DllNotFoundException"/> (or <see cref="FFmpegException"/>) if the natives are missing,
/// unresolved, or the wrong major version.
/// </summary>
public static unsafe class FFmpegDiagnostics
{
    /// <summary>
    /// Loads the core FFmpeg libraries and returns a detailed per-library version summary. Calling each
    /// library's <c>*_version()</c> pulls that specific shared object in, so a successful return proves
    /// every core lib (avutil, avcodec, avformat, swscale, swresample) resolved — not just one.
    /// </summary>
    public static string ProbeVersion()
    {
        FFmpegLoader.EnsureBundledNativesLoaded();

        static string V(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        return string.Join(", ",
            $"avutil {V(LibAv.avutil_version())}",
            $"avcodec {V(LibAv.avcodec_version())}",
            $"avformat {V(LibAv.avformat_version())}",
            $"swscale {V(LibAv.swscale_version())}",
            $"swresample {V(LibAv.swresample_version())}");
    }

    /// <summary>
    /// A short, user-facing FFmpeg version string for the Help ▸ About dialog (e.g. <c>"FFmpeg 8.1"</c>),
    /// derived from <c>av_version_info()</c> with the libavcodec so-version as a fallback.
    /// </summary>
    public static string DisplayVersion()
    {
        // Purely informational (the About box): the module initializer has already registered the resolver
        // and preloaded any bundled natives, so we read the version directly without the startup version
        // guard — callers wrap this so a missing FFmpeg degrades gracefully rather than throwing here.
        string info = Marshal.PtrToStringUTF8(LibAv.av_version_info()) ?? "";
        // av_version_info() is like "8.1", "8.1.1", or "n8.1-31-g…" for git builds. Take the leading
        // numeric token (digits + dots) after any leading 'n'/'N'.
        ReadOnlySpan<char> s = info.AsSpan().TrimStart("nN ".AsSpan());
        int len = 0;
        while (len < s.Length && (char.IsDigit(s[len]) || s[len] == '.')) len++;
        if (len > 0)
            return $"FFmpeg {s[..len]}";

        uint c = LibAv.avcodec_version();
        return $"FFmpeg (libavcodec {c >> 16}.{(c >> 8) & 0xFF}.{c & 0xFF})";
    }
}
