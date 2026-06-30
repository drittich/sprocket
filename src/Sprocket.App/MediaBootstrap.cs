using System;
using System.Diagnostics;
using System.IO;
using Sprocket.App.Proxy;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// Wires up the slice's playback session: opens a media file, builds a one-video-track
/// <see cref="Project"/> over it, and hands back a started <see cref="PlaybackEngine"/>. Real media import
/// (a bin, drag-drop, dialogs) is a later milestone (PLAN steps 11/15); for the slice the app opens the
/// path given on the command line, or generates a sample clip.
/// </summary>
internal static class MediaBootstrap
{
    /// <summary>The created engine, the project it plays (for export), a human-readable status line, and the
    /// session's proxy service (PLAN.md step 18; null when there is no project); or a null engine/project and an
    /// error message. The caller owns and disposes <see cref="Proxy"/> alongside the engine.</summary>
    public readonly record struct Result(PlaybackEngine? Engine, Project? Project, string Status, ProxyService? Proxy = null);

    /// <summary>
    /// Opens <c>args[0]</c> (if it is an existing file) or a generated sample and builds the engine over it.
    /// If opening fails — a bad file, or no <c>ffmpeg</c> to generate the sample — the app must not dead-end:
    /// it falls back to an empty, importable project (ARCHITECTURE.md §15) so File ▸ Import / drag-drop still
    /// work and can bring the editor to life, with the reason shown in the status line.
    /// </summary>
    public static Result Create(string[] args)
    {
        string? path = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        try
        {
            return CreateWithMedia(path ?? SampleClip.EnsureExists());
        }
        catch (Exception ex)
        {
            return CreateEmpty(path, ex.Message);
        }
    }

    /// <summary>Builds a playable session over one opened media file (the populated slice project).</summary>
    private static Result CreateWithMedia(string path)
    {
        {
            // Probe once for format; the engine/mixer open their own per-source decoders via the factories below.
            ProbedMediaInfo info;
            using (MediaSource probe = MediaSource.Open(path))
                info = probe.Info;

            int sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
            var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
            var project = new Project(timeline);

            var mediaId = MediaRefId.New();
            project.MediaPool.Add(new MediaRef(mediaId, path, info));

            // A shared link group ties the video clip to its companion audio so "Linked" moves/blades both
            // together (PLAN.md step 13). The audio clip below joins the same group.
            var linkGroup = Guid.NewGuid();

            var track = new VideoTrack { Name = "V1" };
            var videoClip = new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero) { LinkGroupId = linkGroup };
            // Slice DoD #4/#5: a GPU brightness effect plus a fade in/out, both as SkSL shaders (step 7).
            videoClip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.15));
            videoClip.Effects.Add(new EffectInstance(EffectTypeIds.Fade)
                .Set(EffectParamNames.Opacity, FadeInOut(info.Duration)));
            track.Clips.Add(videoClip);
            timeline.Tracks.Add(track);

            // Master clock: the audio device clock when the source has audio and a device is available
            // (audio is the master, ARCHITECTURE.md §8); otherwise the software wall-clock (video-only).
            (IMasterClock? clock, bool audioWired) = TryCreateAudioClock(project, mediaId, sampleRate, linkGroup);

            // Default-on preview proxies (PLAN.md step 18): the feed factory resolves each source to its proxy
            // when one is ready, else the original — so heavy sources preview immediately and switch transparently.
            var proxy = new ProxyService(project.Settings.UseProxies, project.Settings.ProxyTier);

            // Multi-track preview (PLAN.md step 14): a per-source feed factory lets the engine composite N video
            // tracks; each opens its own decoder. Tracks added at runtime (+ Track) are picked up by the pump.
            var engine = new PlaybackEngine(project, id => OpenVideoFeed(project, id, proxy), clock); // engine owns + disposes the clock
            proxy.ProxyReady += engine.InvalidateSource; // switch the preview onto a proxy the moment it is ready
            engine.Start();
            proxy.Enqueue(project);

            string status =
                $"{Path.GetFileName(path)}  ·  {info.Width}×{info.Height}  ·  " +
                $"{Fps(info.FrameRate):0.##} fps  ·  {info.Duration.ToSeconds():0.0}s  ·  " +
                (audioWired ? "audio master clock" : "no audio (software clock)");
            return new Result(engine, project, status, proxy);
        }
    }

    /// <summary>
    /// Builds an empty but fully-functional session — default settings, one empty video + audio track, a
    /// video-only software clock — so the editor opens ready to import into rather than dead-ending. Used when
    /// no media could be opened (a bad startup file, or no <c>ffmpeg</c> to generate the sample).
    /// </summary>
    private static Result CreateEmpty(string? attemptedPath, string? error)
    {
        var project = new Project(); // default 1080p / 30 fps / 48 kHz timeline
        project.Timeline.Tracks.Add(new VideoTrack { Name = "V1" });
        project.Timeline.Tracks.Add(new AudioTrack { Name = "A1" });

        var proxy = new ProxyService(project.Settings.UseProxies, project.Settings.ProxyTier);
        var engine = new PlaybackEngine(project, id => OpenVideoFeed(project, id, proxy), (IMasterClock?)null);
        proxy.ProxyReady += engine.InvalidateSource;
        engine.Start();

        string status = attemptedPath is not null
            ? $"Could not open {Path.GetFileName(attemptedPath)}: {error}  ·  opened an empty project — use File ▸ Import to add media"
            : "No media loaded — use File ▸ Import to add a video";
        return new Result(engine, project, status, proxy);
    }

    /// <summary>
    /// Builds a playable session over an <em>already-constructed</em> project — a project freshly loaded from
    /// disk (File ▸ Open) or a new empty one (File ▸ New), PLAN.md step 16c. Unlike <see cref="CreateWithMedia"/>
    /// it never mutates the project (no tracks/clips/effects are added) — it just opens decoders for whatever
    /// the project already references and an audio master clock when an audio-bearing source is present.
    /// </summary>
    public static Result CreateForProject(Project project, string status)
    {
        ArgumentNullException.ThrowIfNull(project);

        (IMasterClock? clock, _) = TryCreateAudioClockForProject(project);
        var proxy = new ProxyService(project.Settings.UseProxies, project.Settings.ProxyTier);
        var engine = new PlaybackEngine(project, id => OpenVideoFeed(project, id, proxy), clock); // engine owns + disposes the clock
        proxy.ProxyReady += engine.InvalidateSource;
        engine.Start();
        proxy.Enqueue(project);
        return new Result(engine, project, status, proxy);
    }

    /// <summary>
    /// Builds an audio master clock over an existing project's audio tracks (used by <see cref="CreateForProject"/>
    /// for New/Open), or returns <c>(null, false)</c> so the engine falls back to its software clock — when no
    /// audio track references an audio-bearing source, or no audio device is available. The mixer resolves a PCM
    /// reader per source on demand, so it already mixes N audio tracks; offline sources mix as silence (§15).
    /// </summary>
    private static (IMasterClock? clock, bool audioWired) TryCreateAudioClockForProject(Project project)
    {
        int sampleRate = project.Timeline.SampleRate > 0 ? project.Timeline.SampleRate : 48000;
        const int channels = 2; // stereo output; sources are up/downmixed at decode

        bool hasAudio = project.Timeline.AudioTracks.Any(
            t => t.Clips.Any(c => project.MediaPool.Get(c.MediaRefId) is { Info.HasAudio: true }));
        if (!hasAudio)
            return (null, false);

        OpenAlAudioOutput? output = null;
        try
        {
            var mixer = new AudioMixer(sampleRate, channels, id => OpenPcmReader(project, id, sampleRate, channels));
            output = new OpenAlAudioOutput();
            output.Configure(sampleRate, channels);
            return (new AudioEngine(output, mixer, project), true); // the engine takes ownership
        }
        catch
        {
            output?.Dispose();
            return (null, false); // degrade to the software clock; video still plays
        }
    }

    /// <summary>
    /// Builds the companion audio track + audio master clock for the source, or returns <c>(null, false)</c> so
    /// the engine falls back to its default <c>SoftwareClock</c> — when the source has no audio, or no audio
    /// device is available. Failures degrade gracefully (ARCHITECTURE.md §15): a missing device must not stop
    /// playback. The mixer resolves a PCM reader per source on demand, so it already mixes N audio tracks.
    /// </summary>
    private static (IMasterClock? clock, bool audioWired) TryCreateAudioClock(
        Project project, MediaRefId mediaId, int sampleRate, Guid linkGroup)
    {
        if (project.MediaPool.Get(mediaId) is not { Info.HasAudio: true })
            return (null, false);

        const int channels = 2; // stereo output; the source is upmixed/downmixed at decode
        OpenAlAudioOutput? output = null;
        try
        {
            var audioTrack = new AudioTrack { Name = "A1" };
            var audioClip = new Clip(mediaId, Timecode.Zero, project.Timeline.Duration, Timecode.Zero) { LinkGroupId = linkGroup };
            // The same fade envelope drives audio gain in the mixer (§6) as drives video alpha (§7).
            audioClip.Effects.Add(new EffectInstance(EffectTypeIds.Fade)
                .Set(EffectParamNames.Opacity, FadeInOut(project.Timeline.Duration)));
            audioTrack.Clips.Add(audioClip);
            project.Timeline.Tracks.Add(audioTrack);

            // Per-source PCM readers (mirrors the export path) — the mixer owns + disposes them and sums N layers.
            var mixer = new AudioMixer(sampleRate, channels, id => OpenPcmReader(project, id, sampleRate, channels));

            output = new OpenAlAudioOutput();
            output.Configure(sampleRate, channels);

            // The engine takes ownership: it disposes the mixer (which disposes the readers) and the output.
            return (new AudioEngine(output, mixer, project), true);
        }
        catch
        {
            output?.Dispose();
            return (null, false); // degrade to the software clock; video still plays
        }
    }

    /// <summary>Opens a video frame feed for a source, or <c>null</c> for an offline / no-video source (the engine
    /// then contributes no layer for that track). The feed opens the source's best-available file — its proxy when
    /// ready, else the original (PLAN.md step 18). Each call opens its own decoder; the feed owns + disposes it.</summary>
    private static IVideoFrameFeed? OpenVideoFeed(Project project, MediaRefId id, ProxyService? proxy) =>
        OpenVideoFeed(project.MediaPool.Get(id), proxy);

    /// <summary>Opens a standalone video frame feed for a single source (reused by the Source monitor, PLAN.md
    /// step 17), or <c>null</c> for an offline / no-video source. When <paramref name="proxy"/> is given it opens
    /// the best-available file (proxy when ready, else original); otherwise the original. Each call opens its own
    /// decoder; the feed owns + disposes it.</summary>
    internal static IVideoFrameFeed? OpenVideoFeed(MediaRef? media, ProxyService? proxy = null)
    {
        if (media is not { Info.HasVideo: true })
            return null;
        string path = proxy?.BestPath(media) ?? media.AbsolutePath;
        try
        {
            return new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(path)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens a PCM reader for the mixer, or <c>null</c> (mixed as silence) for an offline / no-audio
    /// source. The mixer owns + disposes the returned reader.</summary>
    private static IPcmReader? OpenPcmReader(Project project, MediaRefId id, int sampleRate, int channels)
    {
        MediaRef? media = project.MediaPool.Get(id);
        if (media is not { Info.HasAudio: true })
            return null;
        try
        {
            return AudioSource.Open(media.AbsolutePath, sampleRate, channels);
        }
        catch
        {
            return null;
        }
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;

    /// <summary>
    /// A fade-in over the first second and fade-out over the last second of a clip of length
    /// <paramref name="duration"/> (opacity 0→1 … 1→0). Degrades to a plain fade-in/out pair for very short
    /// clips. The opacity drives both video alpha (shader) and audio gain (mixer) so the fade is consistent.
    /// </summary>
    private static AnimatableValue FadeInOut(Timecode duration)
    {
        // Keep the ramps short relative to the clip so a tiny clip still gets a (degenerate) fade.
        Timecode ramp = Timecode.Min(Timecode.FromSeconds(1), new Timecode(duration.Ticks / 2));
        return AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 0.0, Interpolation.Linear),
            new Keyframe(ramp, 1.0, Interpolation.Linear),
            new Keyframe(duration - ramp, 1.0, Interpolation.Linear),
            new Keyframe(duration, 0.0, Interpolation.Linear),
        ]);
    }
}

/// <summary>
/// Ensures a 1080p sample clip exists to play when no file is given on the command line. Generated once with
/// the <c>ffmpeg</c> CLI into the app's output directory and cached (mirrors the spike's test asset).
/// </summary>
internal static class SampleClip
{
    public static string EnsureExists()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets");
        string path = Path.Combine(dir, "sample.mp4");

        // Reuse a cached sample only if it actually opens. A partial/corrupt file left by an earlier
        // interrupted generation (e.g. the app was killed mid-encode) must not be handed back — it would
        // fail at open with a bare "Invalid data" and the cache would keep failing on every launch.
        if (File.Exists(path) && CanOpen(path))
            return path;

        Directory.CreateDirectory(dir);
        Generate(path);
        return path;
    }

    /// <summary>True if the file can be opened and has a decodable video stream.</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
            return source.HasVideo;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates the sample clip via the ffmpeg CLI, writing to a temp file and promoting it into place only
    /// after a clean exit — so an interrupted or failed run never leaves a corrupt <c>sample.mp4</c> behind.
    /// </summary>
    private static void Generate(string path)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $"sample.{Guid.NewGuid():N}.tmp.mp4");

        // mandelbrot is an infinite source, so the duration is bounded by -t (mirrors the
        // `ffmpeg -f lavfi -i mandelbrot=size=1920x1080:rate=30 -t 10 -c:v libx264` recipe). A
        // brown-noise audio track rides alongside so the audio master clock path still demos.
		string preferredArgs =
			"-y " +
			"-f lavfi -i \"mandelbrot=size=1920x1080:rate=30,format=yuv420p\" " +
			"-f lavfi -i \"anoisesrc=color=brown:sample_rate=48000:duration=30,volume=0.015\" " +
			$"-t 30 -c:v libx264 -preset veryfast -crf 30 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{temp}\"";

        string fallbackArgs =
            "-y -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 " +
            "-f lavfi -i sine=frequency=440:sample_rate=48000:duration=10 " +
            $"-c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{temp}\"";

        try
        {
            try
            {
                RunFfmpeg(temp, preferredArgs, "preferred sample");
            }
            catch (Exception preferredError)
            {
                TryDelete(temp);

                try
                {
                    RunFfmpeg(temp, fallbackArgs, "fallback sample");
                }
                catch (Exception fallbackError)
                {
                    TryDelete(temp);
                    throw new InvalidOperationException(
                        "ffmpeg failed to generate the sample clip with both the preferred and fallback recipes.\n" +
                        $"Preferred:\n{preferredError.Message}\n\n" +
                        $"Fallback:\n{fallbackError.Message}");
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void RunFfmpeg(string tempPath, string args, string label)
    {
        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        string stderr;
        int exitCode;
        try
        {
            using Process? process = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    "No media path given and the ffmpeg CLI was not found to generate a sample clip. " +
                    "Pass a video file path as the first argument.");

            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (exitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0 || !CanOpen(tempPath))
        {
            TryDelete(tempPath);
            string tail = stderr.Length > 500 ? stderr[^500..] : stderr;
            throw new InvalidOperationException(
                $"ffmpeg failed to generate the {label} (exit code {exitCode})." +
                (tail.Length > 0 ? $"\n{tail}" : ""));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
