using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// The render-cache playback splice (PLAN.md step 32, ARCHITECTURE.md §20): while the playhead is inside a valid
/// cached segment the engine decodes the pre-rendered intermediate through a single synthetic player (the
/// per-track decoders idle), leaving the range presents the live per-track layers again with a re-seek, and an
/// invalidated / unreadable segment falls back to live compositing. Driven deterministically by stepping
/// <see cref="PlaybackEngine.PumpOnceAsync"/> against the real decode fixture.
/// </summary>
public class RenderCachePlaybackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A mutable fake cache: one segment (or none). Thread-safe enough for the test (snapshot field).</summary>
    private sealed class FakeRenderCache : IVideoRenderCache
    {
        public volatile object? Segment; // boxed CachedRenderSegment, or null

        public CachedRenderSegment? ResolveAt(Timecode position) =>
            Segment is CachedRenderSegment seg && seg.Contains(position) ? seg : null;
    }

    /// <summary>Two video tracks over the fixture (so live compositing presents TWO layers, while cached playback
    /// presents exactly ONE), plus a fake cache whose segment covers the middle of the timeline and whose
    /// "intermediate" is the fixture file itself.</summary>
    private static (Project project, FakeRenderCache cache, Func<MediaRefId, IVideoFrameFeed?> factory) BuildSession()
    {
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(TestVideo.Path))
            info = probe.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));

        for (int i = 0; i < 2; i++)
        {
            var track = new VideoTrack { Name = $"V{i + 1}" };
            track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
            timeline.Tracks.Add(track);
        }

        var cache = new FakeRenderCache
        {
            Segment = new CachedRenderSegment(
                MediaRefId.New(), Timecode.FromSeconds(0.5), Timecode.FromSeconds(1.0), TestVideo.Path),
        };

        Func<MediaRefId, IVideoFrameFeed?> factory = mediaId =>
        {
            MediaRef? media = project.MediaPool.Get(mediaId);
            return media is { Info.HasVideo: true }
                ? new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(media.AbsolutePath)))
                : null;
        };
        return (project, cache, factory);
    }

    private static IReadOnlyList<PresentedVideoLayer> Layers(PlaybackEngine engine)
    {
        IReadOnlyList<PresentedVideoLayer> captured = [];
        engine.UseLayers(layers => captured = layers.ToList());
        return captured;
    }

    [Fact]
    public async Task Inside_A_Cached_Segment_The_Composite_Is_The_Single_Cached_Layer()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, FakeRenderCache cache, var factory) = BuildSession();
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));
        engine.RenderCache = cache;

        // Outside the segment: live compositing, two track layers.
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(2, Layers(engine).Count);

        // Inside the segment: exactly one layer — the cached frame, effects baked in (none carried), full opacity.
        engine.SeekTo(Timecode.FromSeconds(0.75));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        IReadOnlyList<PresentedVideoLayer> cached = Layers(engine);
        PresentedVideoLayer layer = Assert.Single(cached);
        Assert.NotEqual(0, layer.Pixels);
        Assert.Empty(layer.Effects);
        Assert.Equal(1.0, layer.Opacity);
        Assert.Equal(BlendMode.Normal, layer.BlendMode);
        // The cached frame maps timeline 0.75 s → file time 0.25 s (the segment starts at 0.5 s).
        Assert.True(layer.Pts <= Timecode.FromSeconds(0.30), $"cache frame at {layer.Pts.ToSeconds():0.###}s");

        // Leaving the segment resumes live compositing (the players re-seek on the boundary crossing).
        engine.SeekTo(Timecode.FromSeconds(1.5));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(2, Layers(engine).Count);
    }

    [Fact]
    public async Task Invalidating_The_Segment_Falls_Back_To_Live_Compositing_On_The_Next_Pump()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, FakeRenderCache cache, var factory) = BuildSession();
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));
        engine.RenderCache = cache;

        engine.SeekTo(Timecode.FromSeconds(0.75));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Single(Layers(engine));

        // An edit invalidated the segment (the service stops resolving it): the very next pump composites live.
        cache.Segment = null;
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(2, Layers(engine).Count);
    }

    [Fact]
    public async Task An_Unreadable_Intermediate_Degrades_To_Live_Compositing()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, FakeRenderCache cache, var factory) = BuildSession();
        cache.Segment = new CachedRenderSegment(
            MediaRefId.New(), Timecode.FromSeconds(0.5), Timecode.FromSeconds(1.0),
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4"));
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));
        engine.RenderCache = cache;

        engine.SeekTo(Timecode.FromSeconds(0.75));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(2, Layers(engine).Count); // fell back live; no black frame, no crash (§15)
    }

    [Fact]
    public async Task The_Cache_Feed_Is_Opened_Once_Per_Segment_Not_Per_Pump()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, FakeRenderCache cache, var factory) = BuildSession();
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));
        engine.RenderCache = cache;

        int opens = 0;
        engine.CacheFeedOpener = path =>
        {
            opens++;
            return new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(path)));
        };

        engine.SeekTo(Timecode.FromSeconds(0.6));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        engine.SeekTo(Timecode.FromSeconds(0.9)); // still inside the same segment
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        Assert.Equal(1, opens);
    }
}
