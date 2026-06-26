using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sprocket.Media;

/// <summary>
/// Loads the FFmpeg native libraries that a shipped build bundles next to the executable
/// (ARCHITECTURE.md §11). The app sets no <c>RootPath</c>, so FFmpeg resolves through the OS loader —
/// and the bundled libraries depend on <em>each other</em> (e.g. libavcodec needs libswresample). With
/// no rpath and no <c>LD_LIBRARY_PATH</c>, the loader won't search a library's own directory for those
/// siblings, so a plain "drop the files next to the exe" bundle fails to load.
/// <para>
/// The fix is to pre-load the core libraries <b>in dependency order</b> from the application directory:
/// once each library is resident under its soname, a later library's <c>NEEDED</c> entries (and Sdcb's
/// own P/Invokes) bind to the already-loaded modules. This is a no-op on Windows, where the Sdcb runtime
/// NuGet embeds the natives, and during local dev, where no loose FFmpeg libs sit beside the assembly.
/// </para>
/// </summary>
public static partial class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool _done;

    // Core FFmpeg libraries in dependency order — each depends only on those before it. Loading them in
    // this order guarantees every library's siblings are already resident when it (or Sdcb) loads it.
    private static readonly string[] OrderedStems =
        ["avutil", "swresample", "swscale", "postproc", "avcodec", "avformat", "avfilter", "avdevice"];

    [GeneratedRegex(@"\.so\.\d+$")]
    private static partial Regex LinuxSoname();

    /// <summary>
    /// Idempotently pre-loads any FFmpeg native libraries bundled next to the application. Safe to call
    /// repeatedly and on every platform; does nothing when no bundled libs are present.
    /// </summary>
    public static void EnsureBundledNativesLoaded()
    {
        lock (Gate)
        {
            if (_done) return;
            _done = true;

            string baseDir = AppContext.BaseDirectory;
            foreach (string stem in OrderedStems)
            {
                string? lib = FindBundledLib(baseDir, stem);
                if (lib is not null)
                    NativeLibrary.TryLoad(lib, out _);
            }
        }
    }

    // Locate the on-disk file for one FFmpeg library, preferring the soname the loader binds against
    // (libavcodec.so.61) over the fully-versioned file (libavcodec.so.61.19.101) or macOS .dylib.
    private static string? FindBundledLib(string dir, string stem)
    {
        // Linux soname, e.g. libavcodec.so.61
        string? soname = Directory.GetFiles(dir, $"lib{stem}.so.*")
            .FirstOrDefault(f => LinuxSoname().IsMatch(f));
        if (soname is not null) return soname;

        // macOS (libavcodec.61.dylib / libavcodec.dylib) and the unversioned Linux symlink fallback.
        foreach (string pattern in new[] { $"lib{stem}.*.dylib", $"lib{stem}.dylib", $"lib{stem}.so" })
        {
            string match = Directory.GetFiles(dir, pattern).FirstOrDefault() ?? "";
            if (match.Length > 0) return match;
        }
        return null;
    }
}
