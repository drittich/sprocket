using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// Resolves and loads the FFmpeg 8 native libraries the hand-rolled binding (<see cref="LibAv"/>) P/Invokes
/// into (ARCHITECTURE.md §11). Two jobs:
/// <list type="number">
/// <item>A <see cref="NativeLibrary.SetDllImportResolver"/> on this assembly maps each bare import stem
/// (<c>"avcodec"</c>) to the bundled, version-suffixed file (<c>avcodec-62.dll</c> / <c>libavcodec.so.62</c>
/// / <c>libavcodec.62.dylib</c>) found beside the executable — or in <c>%SPROCKET_FFMPEG8_DIR%</c> for
/// local dev/test where the natives aren't copied into the output.</item>
/// <item>It pre-loads the core libs <b>in dependency order</b> so each library's siblings are already
/// resident when the OS loader binds a later library's <c>NEEDED</c> imports (which it resolves itself,
/// not through the managed resolver) — the no-rpath bundling path a shipped build depends on.</item>
/// </list>
/// A <b>version guard</b> then rejects a wrong-major FFmpeg loudly: FFmpeg 8 is <c>libavcodec 62</c>; a
/// stray FFmpeg 7 (the macOS Homebrew failure that prompted the migration) fails with a clear message
/// instead of a confusing <c>DllNotFoundException</c> deep in a decode call.
/// </summary>
public static partial class FFmpegLoader
{
    private const int RequiredAvcodecMajor = 62; // FFmpeg 8.x

    private static readonly object Gate = new();
    private static readonly string[] OrderedStems =
        ["avutil", "swresample", "swscale", "postproc", "avcodec", "avformat", "avfilter", "avdevice"];
    private static readonly Dictionary<string, string> StemToPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IntPtr> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static bool _resolverReady;
    private static bool _versionChecked;

    [GeneratedRegex(@"\.so\.\d+$")]
    private static partial Regex LinuxSoname();
    [GeneratedRegex(@"^lib")]
    private static partial Regex LeadingLib();
    [GeneratedRegex(@"[-.]\d+(\.\d+)*$")]
    private static partial Regex TrailingVersion();

    /// <summary>Registers the resolver as early as possible so even a direct <see cref="LibAv"/> call
    /// (e.g. a diagnostics probe) resolves before <see cref="EnsureBundledNativesLoaded"/> runs. Registering
    /// a native-library resolver before the first P/Invoke is the intended advanced use of a module
    /// initializer; the work is a cheap, exception-free best-effort scan.</summary>
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
    internal static void ModuleInit() => RegisterAndPreload();
#pragma warning restore CA2255

    /// <summary>
    /// Idempotently registers the resolver, pre-loads any bundled FFmpeg natives in dependency order, and
    /// verifies the loaded FFmpeg is major 8. Safe to call repeatedly and on every platform. Throws
    /// <see cref="FFmpegException"/> if a wrong-major FFmpeg resolves or no FFmpeg can be loaded.
    /// </summary>
    public static void EnsureBundledNativesLoaded()
    {
        RegisterAndPreload();
        VerifyVersion();
    }

    private static void RegisterAndPreload()
    {
        lock (Gate)
        {
            if (_resolverReady) return;
            _resolverReady = true;

            foreach (string dir in SearchDirectories())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string stem in OrderedStems)
                {
                    if (StemToPath.ContainsKey(stem)) continue;
                    string? file = FindBundledLib(dir, stem);
                    if (file is not null) StemToPath[stem] = file;
                }
            }

            NativeLibrary.SetDllImportResolver(typeof(FFmpegLoader).Assembly, Resolve);

            // Pre-load in dependency order so native→native imports bind to already-resident modules.
            foreach (string stem in OrderedStems)
                if (StemToPath.TryGetValue(stem, out string? path) && NativeLibrary.TryLoad(path, out IntPtr h))
                    Loaded[stem] = h;
        }
    }

    private static void VerifyVersion()
    {
        lock (Gate)
        {
            if (_versionChecked) return;

            uint version;
            try
            {
                version = LibAv.avcodec_version();
            }
            catch (DllNotFoundException ex)
            {
                throw new FFmpegException("FFmpeg load", 0,
                    "no FFmpeg libraries found. Bundle the FFmpeg 8 natives beside the executable " +
                    "or set %SPROCKET_FFMPEG8_DIR% to their directory. " + ex.Message);
            }

            int major = (int)(version >> 16);
            _versionChecked = true;
            if (major != RequiredAvcodecMajor)
                throw new FFmpegException("FFmpeg version check", 0,
                    $"libavcodec major {major} found, but Sprocket requires FFmpeg 8 (libavcodec {RequiredAvcodecMajor}). " +
                    "Install/bundle FFmpeg 8, or point %SPROCKET_FFMPEG8_DIR% at an FFmpeg 8 build.");
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        string? env = Environment.GetEnvironmentVariable("SPROCKET_FFMPEG8_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string stem = StemOf(libraryName);
        if (Loaded.TryGetValue(stem, out IntPtr h)) return h;
        if (StemToPath.TryGetValue(stem, out string? path) && NativeLibrary.TryLoad(path, out h))
        {
            lock (Gate) Loaded[stem] = h;
            return h;
        }
        return IntPtr.Zero; // fall through to default OS resolution (e.g. a system-installed FFmpeg)
    }

    // Locate the on-disk file for one FFmpeg library, preferring the soname the loader binds against
    // (libavcodec.so.62) over the fully-versioned file (libavcodec.so.62.3.100) or macOS .dylib.
    private static string? FindBundledLib(string dir, string stem)
    {
        string? soname = Directory.GetFiles(dir, $"lib{stem}.so.*")
            .FirstOrDefault(f => LinuxSoname().IsMatch(f));
        if (soname is not null) return soname;

        foreach (string pattern in new[] { $"{stem}-*.dll", $"lib{stem}.*.dylib", $"lib{stem}.dylib", $"lib{stem}.so", $"{stem}.dll" })
        {
            string match = Directory.GetFiles(dir, pattern).FirstOrDefault() ?? "";
            if (match.Length > 0) return match;
        }
        return null;
    }

    // "libavcodec.so.62" / "avcodec-62.dll" / "avcodec-62" / "avcodec" -> "avcodec"
    private static string StemOf(string name)
    {
        string s = Path.GetFileName(name);
        int soIdx = s.IndexOf(".so", StringComparison.Ordinal);
        s = soIdx >= 0 ? s[..soIdx] : Path.GetFileNameWithoutExtension(s);
        s = LeadingLib().Replace(s, "");
        s = TrailingVersion().Replace(s, "");
        return s;
    }
}
