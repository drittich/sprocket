using System;
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
/// The composition root's session factory: builds a <see cref="Project"/> + a started
/// <see cref="PlaybackEngine"/> for launch (an empty project, or a file passed on the command line), for
/// File ▸ New / Open / Open Sample Project, and exposes the per-source feed/PCM factories the engine and
/// mixer pull through. Launch opens an empty, importable project — it no longer generates a sample clip
/// (the bundled demo clip is reached via File ▸ Open Sample Project / <see cref="SampleMediaPath"/>).
/// </summary>
internal static class MediaBootstrap
{
    /// <summary>The created engine, the project it plays (for export), a human-readable status line, and the
    /// session's proxy service (PLAN.md step 18; null when there is no project); or a null engine/project and an
    /// error message. The caller owns and disposes <see cref="Proxy"/> alongside the engine.</summary>
    public readonly record struct Result(PlaybackEngine? Engine, Project? Project, string Status, ProxyService? Proxy = null);

    /// <summary>
    /// Builds the launch session. With a playable media file given on the command line (<c>args[0]</c>), opens a
    /// session over it; otherwise opens an empty, importable project (ARCHITECTURE.md §15) so File ▸ Import /
    /// drag-drop / Open Sample Project bring the editor to life. Launch no longer generates a sample clip, so
    /// there is no slow first-run path to cover. If opening a command-line file fails the app still degrades to an
    /// empty project rather than dead-ending, with the reason shown in the status line.
    /// </summary>
    public static Result Create(string[] args)
    {
        string? path = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        if (path is null)
            return CreateEmpty(attemptedPath: null, error: null);
        try
        {
            Project project = BuildProjectFromMedia(path);
            return CreateForProject(project, DescribeMedia(path, project.Timeline));
        }
        catch (Exception ex)
        {
            return CreateEmpty(path, ex.Message);
        }
    }

    /// <summary>
    /// Builds a project model over one media file: a video track with the clip and — when the source carries
    /// audio — a linked companion audio clip on a sibling track, so "Linked" moves/blades both together
    /// (PLAN.md step 13). No engine is built here; the composition root opens decoders + the audio master clock
    /// via <see cref="CreateForProject"/> when the session is swapped in. Probing throws on an unopenable file.
    /// Shared by launch (a command-line file) and File ▸ Open Sample Project.
    /// </summary>
    public static Project BuildProjectFromMedia(string path)
    {
        // Probe once for format; the engine/mixer open their own per-source decoders when the session is built.
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(path))
            info = probe.Info;

        int sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
        var project = new Project(timeline);

        var mediaId = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(mediaId, path, info));

        // A shared link group ties the video clip to its companion audio so edits move/blade both together.
        var linkGroup = Guid.NewGuid();

        var videoTrack = new VideoTrack { Name = "V1" };
        videoTrack.Clips.Add(new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero) { LinkGroupId = linkGroup });
        timeline.Tracks.Add(videoTrack);

        // Always give the project an audio track; lay the companion clip on it only when the source has audio.
        var audioTrack = new AudioTrack { Name = "A1" };
        if (info.HasAudio)
            audioTrack.Clips.Add(new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero) { LinkGroupId = linkGroup });
        timeline.Tracks.Add(audioTrack);

        return project;
    }

    /// <summary>A status line summarising an opened media file: name · resolution · fps · duration.</summary>
    private static string DescribeMedia(string path, Timeline timeline) =>
        $"{Path.GetFileName(path)}  ·  {timeline.Resolution.Width}×{timeline.Resolution.Height}  ·  " +
        $"{Fps(timeline.FrameRate):0.##} fps  ·  {timeline.Duration.ToSeconds():0.0}s";

    /// <summary>
    /// The bundled demo clip copied next to the executable (Sprocket.App.csproj <c>Content</c>), or
    /// <see langword="null"/> when it is not present (e.g. a build that did not copy the asset). Used by
    /// File ▸ Open Sample Project.
    /// </summary>
    public static string? SampleMediaPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Samples", "sample.mp4");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Builds an empty but fully-functional session — default settings, one empty video + audio track, a
    /// video-only software clock — so the editor opens ready to import into rather than dead-ending. This is the
    /// normal launch state (no command-line file), and also the fallback when a command-line file could not be
    /// opened (the reason is then shown in the status line).
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
            : "New project — use File ▸ Import to add media, or File ▸ Open Sample Project for a demo clip";
        return new Result(engine, project, status, proxy);
    }

    /// <summary>
    /// Builds a playable session over an <em>already-constructed</em> project — a project freshly loaded from
    /// disk (File ▸ Open), a new empty one (File ▸ New), a sample project (File ▸ Open Sample Project), or one
    /// built by <see cref="BuildProjectFromMedia"/> at launch (PLAN.md step 16c). It never mutates the project
    /// (no tracks/clips are added) — it just opens decoders for whatever the project already references and an
    /// audio master clock when an audio-bearing source is present.
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
}
