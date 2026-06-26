using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Deterministic tests for the multi-track playback engine (PLAN.md step 14): the per-source feed-factory
/// constructor drives one <see cref="VideoTrackPlayer"/> per video track and <see cref="PlaybackEngine.UseLayers"/>
/// exposes them bottom→top for the preview to composite. Driven by stepping <see cref="PlaybackEngine.PumpOnceAsync"/>
/// with a parked clock — no background loop, no real-time sleeping.
/// </summary>
public class MultiTrackPlaybackTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Builds a project with <paramref name="videoTracks"/> video tracks, each carrying a full-length clip
    /// over the fixture, plus a feed factory that opens a fresh decoder per source.</summary>
    private static (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) BuildSession(int videoTracks)
    {
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(TestVideo.Path))
            info = probe.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));

        for (int i = 0; i < videoTracks; i++)
        {
            var track = new VideoTrack { Name = $"V{i + 1}" };
            track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
            timeline.Tracks.Add(track);
        }

        Func<MediaRefId, IVideoFrameFeed?> factory = mediaId =>
        {
            MediaRef? media = project.MediaPool.Get(mediaId);
            return media is { Info.HasVideo: true }
                ? new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(media.AbsolutePath)))
                : null;
        };
        return (project, factory);
    }

    private static int LayerCount(PlaybackEngine engine)
    {
        int count = -1;
        engine.UseLayers(layers => count = layers.Count);
        return count;
    }

    [Fact]
    public async Task Composites_One_Layer_Per_Enabled_Video_Track()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(videoTracks: 2);
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        // Both video tracks present a frame, so the preview composites two layers bottom→top.
        Assert.Equal(2, LayerCount(engine));
    }

    [Fact]
    public async Task Disabled_Video_Track_Contributes_No_Layer()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(videoTracks: 2);
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(2, LayerCount(engine));

        // Disable the top track: it must drop out of the composite on the next pump.
        ((VideoTrack)project.Timeline.Tracks[1]).Enabled = false;
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(1, LayerCount(engine));
    }

    [Fact]
    public async Task Layers_Carry_Track_Opacity_And_Blend_In_Z_Order()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(videoTracks: 2);
        ((VideoTrack)project.Timeline.Tracks[1]).Opacity = 0.5;
        ((VideoTrack)project.Timeline.Tracks[1]).BlendMode = BlendMode.Screen;
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        IReadOnlyList<PresentedVideoLayer> captured = [];
        engine.UseLayers(layers => captured = layers.ToList());

        Assert.Equal(2, captured.Count);
        // Bottom→top: V1 (index 0) opaque/Normal, V2 (index 1) the half-opacity Screen track on top.
        Assert.Equal(1.0, captured[0].Opacity);
        Assert.Equal(BlendMode.Normal, captured[0].BlendMode);
        Assert.Equal(0.5, captured[1].Opacity);
        Assert.Equal(BlendMode.Screen, captured[1].BlendMode);
    }

    [Fact]
    public async Task A_Video_Track_Added_At_Runtime_Joins_The_Composite()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession(videoTracks: 1);
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(1, LayerCount(engine));

        // Add a second video track with a clip (as "+ Track" + an edit would) — the pump reconciles it in.
        MediaRefId id = project.MediaPool.Items.First().Id;
        var added = new VideoTrack { Name = "V2" };
        added.Clips.Add(new Clip(id, Timecode.Zero, project.Timeline.Duration, Timecode.Zero));
        project.Timeline.Tracks.Add(added);

        engine.SeekTo(Timecode.Zero); // force the new player to seek + present
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        Assert.Equal(2, LayerCount(engine));
    }
}
