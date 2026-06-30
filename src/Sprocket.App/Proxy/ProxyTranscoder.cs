using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Sprocket.Core.Model;

namespace Sprocket.App.Proxy;

/// <summary>
/// Generates one lower-resolution preview proxy for a source by invoking the <c>ffmpeg</c> CLI out-of-process
/// (PLAN.md step 18). Proxies are video-only (audio always reads from the original through the mixer) and encoded
/// for <b>speed, not size</b> — x264 <c>ultrafast</c>, the documented cross-platform fallback (hardware /
/// all-intra codecs for the cache are a later refinement, step 23c / ARCHITECTURE.md §11).
/// </summary>
/// <remarks>
/// <para><b>Why a separate process, not the in-process <see cref="Sprocket.Media.MediaEncoder"/>:</b> proxy
/// generation runs in the background <em>while</em> the live preview is decoding and the GPU compositor is
/// rendering. Driving a second libav* muxer/encoder in-process alongside that pipeline proved fragile (a native
/// access violation in the muxer). Shelling out to the <c>ffmpeg</c> CLI keeps proxy encoding entirely off our
/// process's FFmpeg state and threads, can't corrupt the
/// live pipelines, and is cleanly cancellable by killing the child. If the <c>ffmpeg</c> CLI isn't on PATH the
/// build simply fails and the source keeps previewing on its original (§15) — no crash, no dead-end.</para>
/// <para>The output is written to a temp file and atomically promoted only on a clean exit, so a cancelled or
/// failed run never leaves a half-written proxy the resolver would mistake for a complete one.</para>
/// </remarks>
internal static class ProxyTranscoder
{
    /// <summary>
    /// Builds the proxy for <paramref name="media"/> at <paramref name="target"/> resolution, writing it to
    /// <paramref name="outputPath"/>. Returns <see langword="true"/> on success. Honours
    /// <paramref name="cancellationToken"/> (kills the child); any failure — bad source, no <c>ffmpeg</c> on PATH,
    /// non-zero exit — returns <see langword="false"/> so the source keeps previewing on its original (§15).
    /// </summary>
    public static bool Generate(MediaRef media, Resolution target, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (target.Width <= 0 || target.Height <= 0)
            return false;

        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";

        // -an: video-only (audio mixes from the original). scale to the fixed proxy tier; ultrafast/CRF 28 = speed.
        string scale = string.Create(CultureInfo.InvariantCulture, $"scale={target.Width}:{target.Height}:flags=bilinear");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -nostdin -loglevel error -i \"{media.AbsolutePath}\" -an -vf {scale} " +
            $"-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p \"{tempPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return false;

            // Wait for completion, but bail out promptly (killing the child) if the session is torn down.
            while (!process.WaitForExit(200))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return false;
                }
            }

            if (process.ExitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                return false;

            File.Move(tempPath, outputPath, overwrite: true); // promote atomically, replacing any stale file
            return true;
        }
        catch
        {
            // ffmpeg not found on PATH, or any spawn/IO failure: no proxy, keep the original.
            if (process is not null)
                TryKill(process);
            return false;
        }
        finally
        {
            process?.Dispose();
            TryDelete(tempPath);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup of the temp file */ }
    }
}
