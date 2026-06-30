using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Regression tests for placing the <b>same source media twice on one track</b> with a gap between the two
/// clips (the bug: after the first instance played, the second showed a black frame). Both clips share one
/// <see cref="MediaRefId"/>, so the track's <see cref="VideoTrackPlayer"/> reuses a single feed across the
/// boundary; it must re-seek that feed to the second clip's in-point rather than leave it where the first
/// clip left it. Driven by stepping <see cref="PlaybackEngine.PumpOnceAsync"/> with a controllable clock —
/// no background loop, no real-time sleeping.
/// </summary>
public class RepeatedClipPlaybackTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps; // 8000 ticks/frame @ 30fps
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Builds a one-track project where the same source appears twice: clip A over source
    /// <c>[0, clipSpan)</c> at timeline 0, then (after <paramref name="gap"/>) clip B over the same source span.
    /// The feed factory opens a fresh decoder per source id — invoked once here, since both clips share an id.</summary>
    private static (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) BuildSession(Timecode clipSpan, Timecode gap)
    {
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(TestVideo.Path))
            info = probe.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));

        var track = new VideoTrack { Name = "V1" };
        Timecode clipBStart = clipSpan + gap;
        track.Clips.Add(new Clip(id, Timecode.Zero, clipSpan, Timecode.Zero));    // A: timeline [0, clipSpan)
        track.Clips.Add(new Clip(id, Timecode.Zero, clipSpan, clipBStart));        // B: timeline [clipBStart, …)
        timeline.Tracks.Add(track);

        Func<MediaRefId, IVideoFrameFeed?> factory = mediaId =>
        {
            MediaRef? media = project.MediaPool.Get(mediaId);
            return media is { Info.HasVideo: true }
                ? new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(media.AbsolutePath)))
                : null;
        };
        return (project, factory);
    }

    /// <summary>The source PTS (in ticks) of the top-most presented frame, or -1 when nothing is presented.</summary>
    private static long CurrentPts(PlaybackEngine engine)
    {
        long pts = -1;
        engine.UseCurrentFrame(f =>
        {
            if (f is { } frame)
                pts = frame.Pts.Ticks;
        });
        return pts;
    }

    [Fact]
    public async Task Second_Instance_Of_Same_Source_Plays_After_Playing_Through_The_Gap()
    {
        // Scenario: the same clip placed twice on one track with a gap; play straight through. After clip A and
        // the gap, the playhead enters clip B (same source) — its first frame must appear, not a black hold.
        using var cts = new CancellationTokenSource(Timeout);
        Timecode clipSpan = Timecode.FromFrames(30, Fps);  // 1s: source frames 0..29
        Timecode gap = Timecode.FromFrames(30, Fps);        // 1s gap → clip B at timeline 2s
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(clipSpan, gap);

        var elapsed = TimeSpan.Zero;
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, factory, clock);

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(0, CurrentPts(engine)); // clip A, frame 0

        engine.Play(); // anchors the clock at elapsed 0

        // Mid clip A.
        elapsed = TimeSpan.FromSeconds(0.5);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(15 * FrameTicks, CurrentPts(engine));

        // Into the gap: the track contributes no frame.
        elapsed = TimeSpan.FromSeconds(1.5);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(-1, CurrentPts(engine));

        // Into clip B (timeline 2.0s ⇒ source 0): its first frame must be presented, not a black hold.
        elapsed = TimeSpan.FromSeconds(2.0);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(0, CurrentPts(engine));

        // And it keeps advancing through clip B (timeline 2.5s ⇒ source 0.5s ⇒ frame 15).
        elapsed = TimeSpan.FromSeconds(2.5);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(15 * FrameTicks, CurrentPts(engine));
    }

    [Fact]
    public async Task Seeking_Into_Second_Instance_Presents_Its_Frame_After_The_First_Reached_End_Of_Source()
    {
        // Scenario: both clips span the WHOLE source, so playing clip A drains the decoder to EOF (it parks).
        // Repositioning the playhead into clip B and resuming must re-seek the parked feed back to clip B's
        // in-point and present a frame — not hold black.
        using var cts = new CancellationTokenSource(Timeout);
        Timecode clipSpan = Timecode.FromFrames(TestVideo.FrameCount, Fps); // full 3s source
        Timecode gap = Timecode.FromFrames(30, Fps);                         // 1s gap → clip B at timeline 4s
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(clipSpan, gap);

        var elapsed = TimeSpan.Zero;
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, factory, clock);

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        // Play clip A to (past) its end so the feed reaches end-of-stream and parks.
        engine.Play();
        elapsed = TimeSpan.FromSeconds(3.0);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        // Reposition into clip B: timeline 5.0s ⇒ source 1.0s ⇒ frame 30.
        elapsed = TimeSpan.FromSeconds(5.0);
        engine.SeekTo(Timecode.FromSeconds(5.0));
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Equal(30 * FrameTicks, CurrentPts(engine));
    }
}
