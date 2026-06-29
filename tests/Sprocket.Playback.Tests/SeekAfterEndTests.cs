using System.Diagnostics;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Reproduction for the reported bug: after playback reaches the end of the timeline, seeking (timeline click /
/// rewind / go-to-start) stops moving the playhead. The timeline playhead and transport scrubber are driven
/// solely by the engine's <see cref="PlaybackEngine.PositionChanged"/>, which the background pump raises every
/// tick. If a single pump iteration throws, the pump task must not die permanently — otherwise PositionChanged
/// never fires again and every later seek is silently dead. The end-of-timeline stop is the one place the pump
/// invokes the master clock's <c>Pause</c>/<c>Seek</c> (real device calls with the audio master clock), so a
/// device hiccup there is exactly what bricks the transport.
/// </summary>
public class SeekAfterEndTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) BuildSession()
    {
        MediaSource source = MediaSource.Open(TestVideo.Path);
        ProbedMediaInfo info = source.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);

        return (project, new RingVideoFrameFeed(new VideoDecodeRing(source)), info);
    }

    private static long CurrentPts(PlaybackEngine engine)
    {
        long pts = -1;
        engine.UseCurrentFrame(f => { if (f is { } frame) pts = frame.Pts.Ticks; });
        return pts;
    }

    /// <summary>
    /// A master clock whose <see cref="Pause"/> throws the first <c>throwCount</c> times — simulating the audio
    /// device throwing during the end-of-timeline stop. <see cref="Now"/> is test-driven so the pump can be
    /// pushed past the end deterministically.
    /// </summary>
    private sealed class FlakyPauseClock(int throwCount) : IMasterClock
    {
        private readonly object _gate = new();
        private long _now;
        private long _anchor;
        private bool _running;
        private int _remainingThrows = throwCount;

        public Timecode Now { get { lock (_gate) return new Timecode(_running ? _now : _anchor); } }
        public bool IsRunning { get { lock (_gate) return _running; } }

        public void SetNow(long ticks) { lock (_gate) _now = ticks; }

        public void Start() { lock (_gate) { _anchor = _now; _running = true; } }

        public void Pause()
        {
            lock (_gate)
            {
                if (_remainingThrows > 0) { _remainingThrows--; throw new InvalidOperationException("simulated device fault on stop"); }
                _anchor = _now;
                _running = false;
            }
        }

        public void Seek(Timecode position) { lock (_gate) { _anchor = position.Ticks; _now = position.Ticks; } }
    }

    [Fact]
    public async Task Pump_Survives_A_Throw_During_End_Stop_So_Seeking_Still_Works()
    {
        var clock = new FlakyPauseClock(throwCount: 1);
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        await using var engine = new PlaybackEngine(project, feed, clock);

        long lastReported = -1;
        engine.PositionChanged += t => Volatile.Write(ref lastReported, t.Ticks);

        engine.Start();
        engine.Play();
        clock.SetNow(info.Duration.Ticks + Timecode.TicksPerSecond); // push the playhead past the end

        // The pump should report the end (clamped to Duration); reaching it triggers the throwing stop.
        await WaitUntil(() => Volatile.Read(ref lastReported) == info.Duration.Ticks, Timeout);

        // Now seek back to the start, exactly as the rewind / go-to-start buttons do.
        engine.SeekTo(Timecode.Zero);

        // If the pump died on the throw, PositionChanged never fires again and this never becomes 0.
        await WaitUntil(() => Volatile.Read(ref lastReported) == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, Volatile.Read(ref lastReported));
        Assert.Equal(0, engine.Position.Ticks);
    }

    /// <summary>
    /// The real hang: once the video stream is exhausted but the playhead is still within the clip's timeline span
    /// (the audio master clock keeps the position just short of Duration), a further pump must not block on a read
    /// the parked decode worker can never satisfy. Before the fix this awaits forever — freezing the playhead and
    /// preventing the end-of-timeline stop, so the marker never moves while audio keeps playing.
    /// </summary>
    [Fact]
    public async Task Pump_After_Source_Exhausted_While_Clip_Active_Does_Not_Block()
    {
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        using (var warmup = new CancellationTokenSource(Timeout))
            await engine.PumpOnceAsync(forcePresent: true, warmup.Token);

        clock.Start();
        // A position past the last decoded frame but still inside the clip (< Duration): the promote loop drains
        // the source to end-of-stream and the prefetch becomes null.
        elapsed = TimeSpan.FromSeconds(info.Duration.ToSeconds()) - TimeSpan.FromMilliseconds(5);
        using (var drain = new CancellationTokenSource(Timeout))
            await engine.PumpOnceAsync(forcePresent: false, drain.Token);

        // Pump again at the same (still-in-clip) position. The parked worker will not produce another frame until
        // a seek, so a blocking read here would hang. A short timeout makes the hang observable as a failure.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
    }

    /// <summary>Regression: seeking back after the decode worker has reached true end-of-stream and parked must
    /// still present the target frame (the underlying decode/seek path, with no clock fault).</summary>
    [Fact]
    public async Task Seek_To_Start_After_Decode_Worker_Reaches_Eof_Presents_Frame_Zero()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));

        feed.Start();

        engine.SeekTo(new Timecode(info.Duration.Ticks - 1)); // last instant of the clip → drains to EOF on pump
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        Assert.Equal(0, CurrentPts(engine));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
            await Task.Delay(15);
    }
}
