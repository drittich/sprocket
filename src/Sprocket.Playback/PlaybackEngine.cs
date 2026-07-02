using System.Diagnostics;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Playback;

/// <summary>Transport state of the <see cref="PlaybackEngine"/>.</summary>
public enum PlaybackState
{
    /// <summary>Not playing; the playhead is parked (initial state, and after reaching the end).</summary>
    Stopped,

    /// <summary>Advancing in real time, driven by the clock.</summary>
    Playing,

    /// <summary>Paused at the current position.</summary>
    Paused,
}

/// <summary>
/// A snapshot of one composited video layer's pixels, valid only for the duration of the
/// <see cref="PlaybackEngine.UseLayers"/>/<see cref="PlaybackEngine.UseCurrentFrame"/> callback (the engine holds
/// the frame lock during it). The pixels live in the decoder's native buffer (no managed copy, ARCHITECTURE.md §1).
/// </summary>
/// <param name="Pixels">Pointer to the RGBA8888 pixels.</param>
/// <param name="RowBytes">Stride in bytes.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Pts">The frame's source presentation time.</param>
/// <param name="Effects">The clip's effect chain, evaluated at the current playhead time (bottom→top).</param>
/// <param name="Opacity">The track's opacity in [0, 1].</param>
/// <param name="BlendMode">How the track blends onto the layers beneath it.</param>
/// <param name="Kind">What produces this layer (media frame / generator / adjustment), PLAN.md step 19.</param>
/// <param name="Generator">The procedural source for a <see cref="LayerKind.Generator"/> layer; otherwise null.
/// For generator/adjustment layers <see cref="Pixels"/> is 0 and <see cref="Width"/>/<see cref="Height"/> carry the
/// sequence resolution.</param>
/// <param name="HasAlpha">Whether this media layer's pixels carry a straight alpha channel (PLAN.md step 26); the
/// preview composites it premultiplied over the layers beneath when set. Meaningful only for <see cref="LayerKind.Media"/>.</param>
public readonly record struct PresentedVideoLayer(
    nint Pixels,
    int RowBytes,
    int Width,
    int Height,
    Timecode Pts,
    IReadOnlyList<ResolvedEffect> Effects,
    double Opacity,
    BlendMode BlendMode,
    LayerKind Kind = LayerKind.Media,
    ResolvedGenerator? Generator = null,
    bool HasAlpha = false);

/// <summary>
/// A snapshot of the single (top-most) presented frame — the back-compatible view for a one-layer consumer.
/// See <see cref="PresentedVideoLayer"/> for the per-field meaning.
/// </summary>
public readonly record struct PresentedFrame(
    nint Pixels,
    int RowBytes,
    int Width,
    int Height,
    Timecode Pts,
    IReadOnlyList<ResolvedEffect> Effects,
    bool HasAlpha = false);

/// <summary>
/// A snapshot of the <see cref="PlaybackEngine"/>'s playback-health counters, for the diagnostics overlay (the
/// View ▸ Playback Statistics window). All counts are cumulative since the engine was created; the consumer derives
/// rates (effective preview fps, drops/sec) from the delta between two snapshots taken a known interval apart.
/// </summary>
/// <param name="PumpIterations">Total pump ticks run (each reconciles + advances every track toward the clock).</param>
/// <param name="FramesPresented">Pump ticks that produced a new/updated composite — i.e. the preview repaints.</param>
/// <param name="FramesDropped">Timeline frames the preview could not present in time and had to skip to keep pace
/// with the clock (ARCHITECTURE.md §8), <b>cumulative since the engine was created</b>. Counted in sequence frames
/// off the playhead, so frames a faster-than-sequence source is correctly downsampled past are NOT counted — only a
/// genuine present-cadence shortfall is. Monotonic, so consumers can derive a drops/second rate from the delta
/// between two snapshots.</param>
/// <param name="FramesDroppedThisSpan">As <paramref name="FramesDropped"/>, but reset at the start of each play span
/// (every <see cref="Play"/>). This is the count to surface as "dropped frames" — a warm-up hiccup banked during an
/// earlier play no longer haunts the readout once a fresh playback begins. A healthy preview shows ~0.</param>
public readonly record struct PlaybackStatistics(
    long PumpIterations, long FramesPresented, long FramesDropped, long FramesDroppedThisSpan);

/// <summary>
/// The playback engine (PLAN.md steps 4/14): drives every enabled video track from a master
/// <see cref="IClock"/>, keeping each track's presented frame in sync by dropping or holding decoded frames
/// (ARCHITECTURE.md §8). Transport (<see cref="Play"/>/<see cref="Pause"/>/<see cref="SeekTo"/>) is callable from
/// the UI thread; a background pump advances one <see cref="VideoTrackPlayer"/> per track and the preview
/// composites their frames top-down (<see cref="UseLayers"/>). Audio sync is handled by the audio master clock.
/// </summary>
/// <remarks>
/// <para><b>Events</b> (<see cref="FramePresented"/>, <see cref="PositionChanged"/>, <see cref="StateChanged"/>,
/// <see cref="PlaybackEnded"/>) are raised on the background pump thread; UI subscribers must marshal to their
/// own thread. <see cref="UseLayers"/>/<see cref="UseCurrentFrame"/> hold the frame lock for the callback so the
/// pump cannot recycle a buffer mid-draw.</para>
/// <para>Two construction modes: a single fixed feed (the slice/test path, one video track) or a per-source feed
/// factory (the app path), where players are reconciled against the timeline's video tracks each pump so
/// <c>+ Track</c> / undo are picked up live.</para>
/// </remarks>
public sealed class PlaybackEngine : IAsyncDisposable
{
    private readonly Project _project;
    private readonly IMasterClock _clock;

    // Frame-pacing state (pump thread only). The pump presents on an absolute wall-clock schedule
    // (_nextPresentTs) so the cadence is regular regardless of per-iteration work; _delay1Ms tracks the real
    // Task.Delay(1) duration so the sleep/spin split adapts to the OS timer granularity (see WaitForNextFrameAsync).
    private long _nextPresentTs;
    private bool _pacingAnchored;
    private double _delay1Ms = 2.0;

    private readonly Func<MediaRefId, IVideoFrameFeed?>? _feedFactory;
    private readonly bool _reconcile;

    private readonly object _frameGate = new();
    private readonly List<VideoTrackPlayer> _players = [];

    // Render-cache playback (ARCHITECTURE.md §20, PLAN.md step 32): while the playhead is inside a valid
    // pre-rendered segment, a single synthetic player decodes the cached intermediate and the per-track players
    // idle — replaying the memoized composite instead of recomputing it. Player/segment are swapped on the pump
    // thread under _frameGate so UseLayers (UI thread) always sees a matched pair.
    private VideoTrackPlayer? _cachePlayer;          // guarded by _frameGate for reads; swapped on the pump thread
    private CachedRenderSegment? _cacheSegment;      // the segment _cachePlayer decodes; guarded by _frameGate
    private bool _wasInCache;                        // pump thread only: whether the previous pump played the cache
    private MediaRefId? _deadCacheId;                // pump thread only: last segment whose file failed to open

    private readonly object _invalidateGate = new();
    private readonly HashSet<MediaRefId> _invalidatedSources = [];

    private readonly object _transportGate = new();
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _endHandled;

    private long _seekGeneration;     // bumped by SeekTo; the pump re-seeks players on change
    private long _lastPumpGen;

    // Diagnostics counters (cumulative; written on the pump thread, read via Interlocked in GetStatistics).
    private long _pumpCount;
    private long _presentCount;
    private long _dropCount;      // cumulative since creation (monotonic; for rate derivation)
    private long _dropCountSpan;  // reset on each Play(); the count to display (current play span only)
    private long _lastPresentedFrame = -1; // timeline frame index of the last present (pump thread only); -1 = no baseline yet
    private volatile bool _dropBaselineReset; // set by Play(): the next present re-baselines drops (skip startup catch-up)
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private bool _started;
    private bool _suspended;
    private bool _disposed;
    private bool _highResTimer;       // whether we currently hold the 1ms OS timer period (raised only while playing)

    /// <summary>
    /// Single-feed engine (slice/test path): plays the project's first enabled video track from
    /// <paramref name="feed"/>. <paramref name="clock"/> is the master clock (audio device clock when audio is
    /// present, ARCHITECTURE.md §8) and defaults to a fresh <see cref="SoftwareClock"/>. If the clock is
    /// <see cref="IAsyncDisposable"/> the engine takes ownership and disposes it on teardown.
    /// </summary>
    public PlaybackEngine(Project project, IVideoFrameFeed feed, IMasterClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(feed);

        _project = project;
        _clock = clock ?? new SoftwareClock();
        _reconcile = false;

        VideoTrack? track = project.Timeline.VideoTracks.FirstOrDefault(t => t.Enabled)
                            ?? project.Timeline.VideoTracks.FirstOrDefault();
        if (track is not null)
            _players.Add(new VideoTrackPlayer(track, feed, _frameGate));
        else
            _ = feed.DisposeAsync(); // no video track to drive; don't leak the feed
    }

    /// <summary>
    /// Multi-track engine (app path): plays every enabled video track, creating each track's feed via
    /// <paramref name="feedFactory"/> (mapping a source media id to a feed, or <c>null</c> for an offline/no-video
    /// source). Players are reconciled against the timeline each pump, so tracks added/removed at runtime are
    /// picked up. <paramref name="clock"/> as in the single-feed constructor.
    /// </summary>
    public PlaybackEngine(Project project, Func<MediaRefId, IVideoFrameFeed?> feedFactory, IMasterClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(feedFactory);

        _project = project;
        _clock = clock ?? new SoftwareClock();
        _feedFactory = feedFactory;
        _reconcile = true;
    }

    /// <summary>
    /// The preview render cache (ARCHITECTURE.md §20, PLAN.md step 32), or <see langword="null"/> (the default)
    /// to always composite live. When set, each pump asks it for the valid pre-rendered segment covering the
    /// playhead; inside one, the engine decodes the cached intermediate through the normal native path (a
    /// synthetic single-layer source — the same seam media and proxies use) and the per-track decoders idle.
    /// This is what lets nested sequences, transition blends, and heavy effect chains preview at full fidelity
    /// once rendered. Settable at any time from the UI thread; honoured only by the factory (app) engine.
    /// Export never consults this (§17).
    /// </summary>
    public IVideoRenderCache? RenderCache { get; set; }

    /// <summary>How a cached segment's intermediate file is opened as a frame feed. Overridable by deterministic
    /// tests (a fake feed avoids native decode); the default opens the file through the normal decode ring.</summary>
    internal Func<string, IVideoFrameFeed?> CacheFeedOpener { get; set; } = OpenCacheFeed;

    /// <summary>Raised after any track's presented frame changes. Fires on the pump thread.</summary>
    public event Action? FramePresented;

    /// <summary>Raised each pump tick with the current timeline position. Fires on the pump thread.</summary>
    public event Action<Timecode>? PositionChanged;

    /// <summary>Raised when the transport state changes. Fires on the pump or calling thread.</summary>
    public event Action<PlaybackState>? StateChanged;

    /// <summary>Raised once when playback reaches the end of the timeline. Fires on the pump thread.</summary>
    public event Action? PlaybackEnded;

    /// <summary>Raised when a pump iteration throws (a decode/clock/device hiccup). The pump swallows the error
    /// and keeps running so the transport stays responsive; subscribers may surface it. Fires on the pump thread.</summary>
    public event Action<Exception>? PumpError;

    /// <summary>The current transport state.</summary>
    public PlaybackState State
    {
        get { lock (_transportGate) return _state; }
    }

    /// <summary>The current playhead position, clamped to the timeline.</summary>
    public Timecode Position => PlaybackMath.ClampToTimeline(_clock.Now, Duration);

    /// <summary>The total timeline duration.</summary>
    public Timecode Duration => _project.Timeline.Duration;

    /// <summary>The active sequence's target frame rate — the cadence the preview aims to present at.</summary>
    public Rational FrameRate => _project.Timeline.FrameRate;

    /// <summary>
    /// A point-in-time snapshot of the engine's playback-health counters for the diagnostics overlay (View ▸
    /// Playback Statistics). Safe to call from any thread; see <see cref="PlaybackStatistics"/>.
    /// </summary>
    public PlaybackStatistics GetStatistics() => new(
        Interlocked.Read(ref _pumpCount),
        Interlocked.Read(ref _presentCount),
        Interlocked.Read(ref _dropCount),
        Interlocked.Read(ref _dropCountSpan));

    /// <summary>
    /// The decode info (codec + hardware device) of the top-most enabled video track with an active clip at the
    /// playhead — i.e. what the preview is currently showing — or <see langword="null"/> when nothing is being
    /// decoded. For the diagnostics overlay. Returns a cached managed snapshot, so it is safe to call from the UI
    /// thread (it never dereferences native decoder state).
    /// </summary>
    public VideoDecodeInfo? GetActiveVideoDecodeInfo()
    {
        Timecode pos = Position;
        lock (_frameGate)
        {
            // Inside a cached range the preview shows the rendered intermediate — report its decode, not the tracks'.
            if (_cacheSegment is { } seg && seg.Contains(pos) && _cachePlayer is { DecodeInfo: { } cacheInfo })
                return cacheInfo;

            VideoDecodeInfo? top = null; // bottom→top: the last match is the top-most layer the preview shows
            foreach (VideoTrack track in _project.Timeline.VideoTracks)
            {
                if (!track.Enabled || track.ResolveActiveClip(pos) is null)
                    continue;
                if (FindPlayer(track)?.DecodeInfo is { } info)
                    top = info;
            }
            return top;
        }
    }

    /// <summary>
    /// Starts the background pump and positions the playhead at the start, so the first frame is presented before
    /// play begins. Feeds are started lazily by their track players on the first pump. Idempotent.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _started = true;

        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
        SeekTo(Timecode.Zero); // position the feeds at the active clips' in-points and load frame 0
    }

    /// <summary>Begins (or resumes) playback. Replays from the start if currently parked at the end.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PlaybackMath.ReachedEnd(Position, Duration))
            SeekTo(Timecode.Zero);

        // The audio master clock starts advancing immediately, but the first video frame of a play span may still
        // be decoding (cold decode warm-up, or the ring re-priming after a pause). The pump's first present then
        // catches up by a frame or two — expected startup catch-up, not a render stutter — so re-baseline the drop
        // accounting on that present rather than banking it as phantom dropped frames. Arm the flag before the
        // clock advances so the pump can't slip a counted present into the gap. Also reset the play-span drop
        // counter so the "dropped frames" readout reflects this playback, not drops banked by an earlier span.
        _dropBaselineReset = true;
        Interlocked.Exchange(ref _dropCountSpan, 0);
        _clock.Start();
        SetState(PlaybackState.Playing);
        lock (_transportGate)
            _endHandled = false;
    }

    /// <summary>Pauses playback at the current position.</summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _clock.Pause();
        SetState(PlaybackState.Paused);
    }

    /// <summary>Toggles between play and pause.</summary>
    public void TogglePlayPause()
    {
        if (State == PlaybackState.Playing)
            Pause();
        else
            Play();
    }

    /// <summary>
    /// Moves the playhead to <paramref name="position"/> (clamped to the timeline). The pump sees the bumped
    /// generation, re-seeks every track's feed, and force-presents the post-seek frames. Keeps the running/paused
    /// state. Safe to call while playing (scrub).
    /// </summary>
    public void SeekTo(Timecode position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Timecode clamped = PlaybackMath.ClampToTimeline(position, Duration);

        _clock.Seek(clamped);
        Interlocked.Increment(ref _seekGeneration);

        lock (_transportGate)
            _endHandled = false;
    }

    /// <summary>
    /// Signals that <paramref name="id"/>'s best-available source file has changed (a preview proxy became ready,
    /// PLAN.md step 18). The next pump rebuilds the feed of any track currently decoding that source, so the
    /// preview transparently switches to the proxy without a seek or a clip edit. Safe to call from any thread;
    /// a no-op for the fixed-feed (slice/test) engine.
    /// </summary>
    public void InvalidateSource(MediaRefId id)
    {
        if (!_reconcile)
            return;
        lock (_invalidateGate)
            _invalidatedSources.Add(id);
    }

    /// <summary>
    /// Steps the playhead <paramref name="delta"/> whole frames (PLAN.md step 17): pauses playback if running,
    /// then seeks to the frame-aligned position. The pump force-presents the post-seek frame so a single step is
    /// frame-accurate. Negative <paramref name="delta"/> steps backward; clamped to the timeline ends.
    /// </summary>
    public void StepFrame(int delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State == PlaybackState.Playing)
            Pause();
        SeekTo(PlaybackMath.StepFrame(Position, _project.Timeline.FrameRate, delta, Duration));
    }

    /// <summary>
    /// Invokes <paramref name="use"/> with the composited video layers (bottom→top in z-order), holding the frame
    /// lock for the duration so the pump cannot recycle a native buffer. Keep the callback short and do not retain
    /// the layers beyond it. The list is empty when no track has a frame to show.
    /// </summary>
    public void UseLayers(Action<IReadOnlyList<PresentedVideoLayer>> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        Timecode pos = Position;
        Resolution res = _project.Timeline.Resolution;
        lock (_frameGate)
        {
            // Inside a valid pre-rendered range the composite IS the cached frame (ARCHITECTURE.md §20): present
            // it as the single layer — effects/opacity/blend are already baked in. While the cache feed is still
            // priming (no frame yet) fall through to the live layers so the preview never goes black.
            if (_cacheSegment is { } seg && seg.Contains(pos) && _cachePlayer?.Current is { } cached)
            {
                use([new PresentedVideoLayer(
                    cached.Pixels, cached.RowBytes, cached.Width, cached.Height, cached.Pts,
                    [], 1.0, BlendMode.Normal)]);
                return;
            }

            var layers = new List<PresentedVideoLayer>(_players.Count);
            // Bottom→top: iterate video tracks in z-order and emit each active clip's layer. Media clips contribute
            // only when their player has a decoded frame; generator/adjustment clips contribute without a decoder
            // (PLAN.md step 19) — the preview draws their content / applies their effects to the lower composite.
            foreach (VideoTrack track in _project.Timeline.VideoTracks)
            {
                if (!track.Enabled)
                    continue;
                Clip? clip = track.ResolveActiveClip(pos);
                if (clip is null)
                    continue;

                IReadOnlyList<ResolvedEffect> effects = RenderGraph.ResolveEffects(clip, pos);
                switch (clip.Kind)
                {
                    case ClipKind.Generator:
                        layers.Add(new PresentedVideoLayer(
                            0, 0, res.Width, res.Height, clip.MapToSource(pos), effects, track.Opacity, track.BlendMode,
                            LayerKind.Generator, RenderGraph.ResolveGenerator(clip.Generator!, pos)));
                        break;

                    case ClipKind.Adjustment:
                        layers.Add(new PresentedVideoLayer(
                            0, 0, res.Width, res.Height, pos, effects, track.Opacity, track.BlendMode,
                            LayerKind.Adjustment));
                        break;

                    case ClipKind.Sequence:
                        // A nested-sequence clip (PLAN.md step 23). Live preview compositing of the child sequence
                        // is deferred to the render cache (step 32); the preview draws a placeholder for the layer
                        // so the clip reads as present (its full composite renders on export and when the child is
                        // opened). No decoder, so it is treated as a synthetic clip for repaint purposes.
                        layers.Add(new PresentedVideoLayer(
                            0, 0, res.Width, res.Height, clip.MapToSource(pos), effects, track.Opacity, track.BlendMode,
                            LayerKind.Sequence));
                        break;

                    default:
                        if (FindPlayer(track)?.Current is { } frame)
                            layers.Add(new PresentedVideoLayer(
                                frame.Pixels, frame.RowBytes, frame.Width, frame.Height, frame.Pts,
                                effects, track.Opacity, track.BlendMode, HasAlpha: frame.HasAlpha));
                        break;
                }
            }
            use(layers);
        }
    }

    /// <summary>
    /// Invokes <paramref name="use"/> with the top-most presented layer as a <see cref="PresentedFrame"/> (or
    /// <c>null</c>), holding the frame lock for the duration. Back-compatible single-layer view.
    /// </summary>
    public void UseCurrentFrame(Action<PresentedFrame?> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        Timecode pos = Position;
        lock (_frameGate)
        {
            if (_cacheSegment is { } seg && seg.Contains(pos) && _cachePlayer?.Current is { } cached)
            {
                use(new PresentedFrame(cached.Pixels, cached.RowBytes, cached.Width, cached.Height, cached.Pts, []));
                return;
            }

            PresentedFrame? top = null;
            // Top-most = last enabled video track (in bottom→top order) that has a frame.
            foreach (VideoTrack track in _project.Timeline.VideoTracks)
            {
                if (!track.Enabled)
                    continue;
                VideoTrackPlayer? player = FindPlayer(track);
                if (player?.Current is not { } frame)
                    continue;
                Clip? clip = track.ResolveActiveClip(pos);
                IReadOnlyList<ResolvedEffect> effects = clip is null ? [] : RenderGraph.ResolveEffects(clip, pos);
                top = new PresentedFrame(frame.Pixels, frame.RowBytes, frame.Width, frame.Height, frame.Pts, effects, frame.HasAlpha);
            }
            use(top);
        }
    }

    private VideoTrackPlayer? FindPlayer(VideoTrack track)
    {
        foreach (VideoTrackPlayer p in _players)
            if (ReferenceEquals(p.Track, track))
                return p;
        return null;
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PumpOnceAsync(forcePresent: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // cancellation is teardown — leave the loop
            }
            catch (Exception ex)
            {
                // One pump iteration failed — e.g. a decode hiccup, or the audio device faulting while the
                // master clock is paused/sought during the end-of-timeline stop. This must NOT kill the pump:
                // a dead pump stops raising PositionChanged, which freezes the playhead and leaves the transport
                // permanently unresponsive (a click on the timeline, and the rewind / go-to-start buttons, would
                // no longer move the position indicator). Surface it and keep pumping so the next tick — and any
                // seek the user issues — recovers.
                PumpError?.Invoke(ex);
            }

            try
            {
                // While playing, wait until the next frame's exact deadline so each frame is presented on the
                // frame grid (smooth cadence). While idle, a coarse poll is enough to stay responsive to seeks.
                if (State == PlaybackState.Playing)
                    await WaitForNextFrameAsync(ct).ConfigureAwait(false);
                else
                    await Task.Delay(16, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Waits until the next frame's scheduled present time, pacing the pump on an <b>absolute</b> wall-clock
    /// schedule (one frame-interval apart) so the present cadence is regular regardless of how long a pump
    /// iteration takes. A fixed sub-frame poll instead aliases against the true frame grid and produces visible
    /// judder even when no frames are dropped (ARCHITECTURE.md §8); A/V sync is still maintained because each
    /// iteration's <see cref="PumpOnceAsync"/> presents the frame matching the master clock (drop/hold).
    /// </summary>
    /// <remarks>
    /// The wait sleeps in 1&#160;ms steps only while comfortably far from the deadline, then busy-spins the final
    /// few ms. Because a step is taken only when the remaining time exceeds the observed <see cref="_delay1Ms"/>
    /// granularity, a coarse OS timer (Windows can ignore <c>timeBeginPeriod</c> for background processes, making
    /// <c>Task.Delay(1)</c> ~15.6&#160;ms) cannot overshoot the deadline — it just spins a little longer. When the
    /// timer is fine the spin is ~2&#160;ms. If the pump falls more than two frames behind it re-anchors instead of
    /// bursting to catch up.
    /// </remarks>
    private async Task WaitForNextFrameAsync(CancellationToken ct)
    {
        Rational fps = _project.Timeline.FrameRate;
        double frameSec = fps.Num > 0 ? (double)fps.Den / fps.Num : 1.0 / 30;
        long frameTicks = Math.Max(1, (long)(frameSec * Stopwatch.Frequency));
        long now = Stopwatch.GetTimestamp();

        if (!_pacingAnchored || now - _nextPresentTs > 2 * frameTicks)
            _nextPresentTs = now + frameTicks; // first frame of a play span, or fell >2 frames behind → re-anchor
        else
            _nextPresentTs += frameTicks;
        _pacingAnchored = true;

        while (true)
        {
            long remTicks = _nextPresentTs - Stopwatch.GetTimestamp();
            if (remTicks <= 0)
                return;
            double remMs = remTicks * 1000.0 / Stopwatch.Frequency;
            if (remMs > _delay1Ms + 2.0)
            {
                long t0 = Stopwatch.GetTimestamp();
                await Task.Delay(1, ct).ConfigureAwait(false);
                double slept = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _delay1Ms = _delay1Ms * 0.9 + slept * 0.1; // EMA of the real timer granularity
            }
            else
            {
                if (ct.IsCancellationRequested)
                    return;
                Thread.SpinWait(40);
            }
        }
    }

    /// <summary>One pump iteration: reconcile players to the tracks, catch each up to the playhead, then report
    /// position/end. Factored out so deterministic tests can step the pump without the real-time delay loop.</summary>
    internal async Task PumpOnceAsync(bool forcePresent, CancellationToken ct)
    {
        long gen = Interlocked.Read(ref _seekGeneration);
        bool seekChanged = gen != _lastPumpGen;
        if (seekChanged)
        {
            _lastPumpGen = gen;
            foreach (VideoTrackPlayer p in _players)
                p.MarkNeedsSeek();
            _cachePlayer?.MarkNeedsSeek();
        }
        bool force = forcePresent || seekChanged;

        if (_reconcile)
            await ReconcilePlayersAsync().ConfigureAwait(false);

        ApplyInvalidations();

        Timecode pos = PlaybackMath.ClampToTimeline(_clock.Now, Duration);

        // Render cache (ARCHITECTURE.md §20): inside a valid pre-rendered segment, decode the cached intermediate
        // through the synthetic cache player and let the per-track decoders idle; everywhere else composite live.
        // Crossing the boundary in either direction re-seeks the side that resumes (its feeds haven't advanced
        // with the playhead) and force-presents so the first frame across the boundary shows immediately.
        bool inCache = await EnsureCacheStateAsync(pos).ConfigureAwait(false);
        if (inCache != _wasInCache)
        {
            foreach (VideoTrackPlayer p in _players)
                p.MarkNeedsSeek();
            _cachePlayer?.MarkNeedsSeek();
            force = true;
            _wasInCache = inCache;
        }

        bool promoted = false;
        if (inCache)
            promoted = await _cachePlayer!.PumpAsync(pos, force, ct).ConfigureAwait(false);
        else
            foreach (VideoTrackPlayer player in _players)
                promoted |= await player.PumpAsync(pos, force, ct).ConfigureAwait(false);

        // Health counters for the diagnostics overlay (cumulative; read via GetStatistics).
        Interlocked.Increment(ref _pumpCount);

        // Generator / adjustment clips have no decoder to "promote" a frame, so they'd never trigger a repaint.
        // Repaint for them when playing (their content/effects may animate) or when a seek forces a present
        // (a scrub onto a title). A static synthetic clip while paused doesn't repaint every idle tick.
        bool synthetic = (State == PlaybackState.Playing || force) && !inCache && HasActiveSyntheticVideoClip(pos);
        if (promoted || synthetic)
        {
            // Dropped frames = timeline frames we couldn't present in time and had to skip to keep pace with the
            // clock (ARCHITECTURE.md §8). Measured off the playhead's timeline-frame index — not the count of
            // decoded source frames skipped — so a clip whose source rate exceeds the sequence rate (e.g. a 60fps
            // clip on a 30fps timeline) is NOT charged for the in-between frames it correctly downsamples away;
            // only a genuine present-cadence shortfall counts. Computed once here (off the playhead) rather than
            // per track, so N video tracks don't multiply a single skipped instant. A forced present (seek/scrub)
            // or the first present of a play span (startup decode/audio warm-up catch-up) is an intentional or
            // expected jump, not a stutter, so it only re-baselines the frame index without counting.
            bool rebaseline = force;
            if (_dropBaselineReset)
            {
                _dropBaselineReset = false;
                rebaseline = true;
            }
            long frame = pos.ToFrameIndex(_project.Timeline.FrameRate);
            if (!rebaseline && _lastPresentedFrame >= 0)
            {
                long skipped = frame - _lastPresentedFrame - 1;
                if (skipped > 0)
                {
                    Interlocked.Add(ref _dropCount, skipped);     // cumulative (rate)
                    Interlocked.Add(ref _dropCountSpan, skipped); // this play span (display)
                }
            }
            _lastPresentedFrame = frame;

            Interlocked.Increment(ref _presentCount); // a new composite was produced → the preview repaints
            FramePresented?.Invoke();
        }

        PositionChanged?.Invoke(pos);
        HandleEnd(pos);
    }

    /// <summary>
    /// Resolves the render-cache segment covering <paramref name="pos"/> and keeps the synthetic cache player in
    /// step with it: builds a player when the playhead enters a (new) segment, tears it down when it leaves or the
    /// segment is invalidated (so the intermediate's file handle is released promptly and Delete Render Files can
    /// remove it). Returns whether the cache covers <paramref name="pos"/> with a decodable feed — when the file
    /// fails to open the segment is remembered as dead and the engine composites live instead (§15). Pump thread only.
    /// </summary>
    private async ValueTask<bool> EnsureCacheStateAsync(Timecode pos)
    {
        CachedRenderSegment? seg = _reconcile ? RenderCache?.ResolveAt(pos) : null;
        if (seg is { } dead && dead.CacheId == _deadCacheId)
            seg = null; // this segment's file failed to open once; don't retry every pump

        if (seg is null)
        {
            if (_cachePlayer is not null)
                await TearDownCachePlayerAsync().ConfigureAwait(false);
            return false;
        }

        CachedRenderSegment target = seg.Value;
        if (_cacheSegment is { } current && current.CacheId == target.CacheId)
            return true; // already decoding this segment (ids are content-addressed, so the file never changes)

        await TearDownCachePlayerAsync().ConfigureAwait(false);

        // Open the intermediate eagerly so a failure is detectable (a lazily-failing factory would leave the
        // engine "in cache" presenting nothing). The fixed-feed player never rebuilds — exactly right here.
        IVideoFrameFeed? feed = CacheFeedOpener(target.FilePath);
        if (feed is null)
        {
            _deadCacheId = target.CacheId;
            return false;
        }

        // A synthetic one-clip track spanning the segment maps timeline→file time through the ordinary clip
        // machinery: the intermediate's timestamps start at zero at the segment's in-point.
        var track = new VideoTrack { Name = "(render cache)" };
        track.Clips.Add(new Clip(target.CacheId, Timecode.Zero, target.End - target.Start, target.Start));
        var player = new VideoTrackPlayer(track, feed, _frameGate);
        lock (_frameGate)
        {
            _cachePlayer = player;
            _cacheSegment = target;
        }
        return true;
    }

    /// <summary>Detaches and disposes the cache player (pump thread). The field swap happens under the frame gate
    /// so the UI's <see cref="UseLayers"/> never sees a player whose buffers are being torn down.</summary>
    private async Task TearDownCachePlayerAsync()
    {
        VideoTrackPlayer? old;
        lock (_frameGate)
        {
            old = _cachePlayer;
            _cachePlayer = null;
            _cacheSegment = null;
        }
        if (old is not null)
            await old.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The default <see cref="CacheFeedOpener"/>: decodes a rendered intermediate through the normal
    /// native decode ring (pixels stay in native buffers, ARCHITECTURE.md §1), or <see langword="null"/> when the
    /// file can't be opened (the engine then composites live, §15).</summary>
    private static IVideoFrameFeed? OpenCacheFeed(string path)
    {
        try
        {
            return new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(path)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether any enabled video track has a decoder-less clip — generator, adjustment, or nested
    /// sequence (PLAN.md step 23) — active at <paramref name="pos"/>; these never "promote" a decoded frame, so the
    /// preview must be told to repaint for them on play / a forced present.</summary>
    private bool HasActiveSyntheticVideoClip(Timecode pos)
    {
        foreach (VideoTrack track in _project.Timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;
            if (track.ResolveActiveClip(pos) is { Kind: ClipKind.Generator or ClipKind.Adjustment or ClipKind.Sequence })
                return true;
        }
        return false;
    }

    /// <summary>Drains the pending source invalidations (from <see cref="InvalidateSource"/>) and asks any player
    /// currently decoding one of those sources to rebuild its feed on this pump. Runs on the pump thread, so the
    /// players' source comparison stays single-threaded.</summary>
    private void ApplyInvalidations()
    {
        MediaRefId[] ids;
        lock (_invalidateGate)
        {
            if (_invalidatedSources.Count == 0)
                return;
            ids = [.. _invalidatedSources];
            _invalidatedSources.Clear();
        }

        foreach (VideoTrackPlayer player in _players)
        {
            if (player.FeedSource is { } src && Array.IndexOf(ids, src) >= 0)
                player.RequestRebuild();
        }
    }

    /// <summary>Adds players for newly-added video tracks and disposes players for removed ones, so runtime
    /// <c>+ Track</c> / undo are reflected. A fresh player seeks on its first pump (it starts needing a seek).</summary>
    private async Task ReconcilePlayersAsync()
    {
        // Remove players whose track is no longer in the timeline.
        for (int i = _players.Count - 1; i >= 0; i--)
        {
            if (!_project.Timeline.VideoTracks.Contains(_players[i].Track))
            {
                VideoTrackPlayer gone = _players[i];
                _players.RemoveAt(i);
                await gone.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Add players for tracks that don't have one yet.
        foreach (VideoTrack track in _project.Timeline.VideoTracks)
        {
            if (FindPlayer(track) is null)
                _players.Add(new VideoTrackPlayer(track, _feedFactory!, _frameGate));
        }
    }

    private void HandleEnd(Timecode pos)
    {
        if (State != PlaybackState.Playing || !PlaybackMath.ReachedEnd(pos, Duration))
            return;

        bool fire;
        lock (_transportGate)
        {
            fire = !_endHandled;
            _endHandled = true;
        }
        if (!fire)
            return;

        // Park the transport at the end. Transition to Stopped first: that can't throw, so even if pausing/seeking
        // the clock faults (the pump loop catches it), the engine can never get wedged in Playing — which would
        // otherwise keep re-entering this path and re-throwing every tick.
        SetState(PlaybackState.Stopped);
        _clock.Pause();
        _clock.Seek(Duration);
        PlaybackEnded?.Invoke();
    }

    private void SetState(PlaybackState state)
    {
        // Decide the OS-timer-resolution action under the same lock that guards the state flip, so a UI-thread
        // transport call and the pump thread's end-of-timeline stop can't leave the 1ms period unbalanced. The
        // native winmm call itself runs outside the lock. Hold the high-res timer only while playing, so the
        // pump's Task.Delay frame-pacing is accurate (the default ~15.6ms Windows timer turns a 16ms pace into
        // ~31ms → judder; see PlaybackTimerResolution).
        bool changed, raise = false, lower = false;
        lock (_transportGate)
        {
            changed = _state != state;
            _state = state;
            if (changed)
            {
                if (state == PlaybackState.Playing && !_highResTimer) { _highResTimer = true; raise = true; }
                else if (state != PlaybackState.Playing && _highResTimer) { _highResTimer = false; lower = true; }
            }
        }
        if (!changed)
            return;
        if (raise) PlaybackTimerResolution.Raise();
        if (lower) PlaybackTimerResolution.Lower();
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Quiesces <b>every in-process decode pipeline this engine drives</b> so an in-process export muxer can run
    /// as the only libav* activity in the process: a second concurrent libav* pipeline crashes the muxer with a
    /// native access violation (the hazard <c>ProxyTranscoder</c> documents — which is why proxy encoding shells
    /// out). It pauses the audio master clock (its feeder then idles without mixing/decoding), stops the pump, and
    /// tears down the per-track decode-ring workers. <see cref="Pause"/> is <b>not</b> sufficient: it only pauses
    /// the clock — the pump and the decode-ring workers keep running. The playhead position is preserved; call
    /// <see cref="Resume"/> to restart playback (paused) afterwards. Idempotent.
    /// </summary>
    /// <remarks>The factory (app) engine recreates and re-seeks its feeds on <see cref="Resume"/>; a fixed-feed
    /// engine cannot rebuild its single feed, so it keeps it and relies on the bounded decode ring parking once the
    /// pump stops consuming.</remarks>
    public async Task SuspendAsync()
    {
        // Lifecycle coordination (often from an export's finally): no-op rather than throw if torn down already
        // (e.g. File ▸ New disposed this engine while an export was in flight).
        if (_disposed || _suspended)
            return;
        _suspended = true;

        // Stop advancing and pause the audio device clock; the audio feeder then idles without mixing/decoding.
        _clock.Pause();
        SetState(PlaybackState.Paused);

        // Stop the pump so it issues no further decode reads, reconciles, or feed rebuilds.
        await StopPumpAsync().ConfigureAwait(false);

        // Tear down the decode-ring workers so NO in-process FFmpeg decode runs alongside the export muxer. Only
        // the factory engine can rebuild its feeds on Resume (the pump's reconcile recreates the players); a
        // fixed feed is left in place — its worker parks once the stopped pump no longer drains the ring.
        if (_reconcile)
        {
            List<VideoTrackPlayer> stale;
            lock (_frameGate)
            {
                stale = [.. _players];
                _players.Clear();
            }
            foreach (VideoTrackPlayer player in stale)
                await player.DisposeAsync().ConfigureAwait(false);

            // The cache player is a decode ring too — it must not run alongside the export muxer either. The
            // pump rebuilds it from the (unchanged, content-addressed) segment on Resume.
            await TearDownCachePlayerAsync().ConfigureAwait(false);
            _wasInCache = false;
        }
    }

    /// <summary>
    /// Restarts the pump after <see cref="SuspendAsync"/>, rebuilding the feeds and presenting the frame at the
    /// preserved playhead. Leaves the transport paused (the caller resumes play if desired). Idempotent.
    /// </summary>
    public void Resume()
    {
        // Symmetric with SuspendAsync: tolerate a teardown that happened during the suspended window.
        if (_disposed || !_suspended)
            return;
        _suspended = false;

        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
        SeekTo(Position); // re-seek so the rebuilt players decode + force-present the current frame
    }

    /// <summary>Cancels the pump and awaits its exit, then clears its handles. Shared by suspend and dispose.</summary>
    private async Task StopPumpAsync()
    {
        _pumpCts?.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _pump = null;
        _pumpCts?.Dispose();
        _pumpCts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Release the 1ms timer period if we were torn down mid-play (SetState's Pause/Stop never ran).
        bool releaseTimer;
        lock (_transportGate) { releaseTimer = _highResTimer; _highResTimer = false; }
        if (releaseTimer)
            PlaybackTimerResolution.Lower();

        await StopPumpAsync().ConfigureAwait(false);

        foreach (VideoTrackPlayer player in _players)
            await player.DisposeAsync().ConfigureAwait(false);
        _players.Clear();
        await TearDownCachePlayerAsync().ConfigureAwait(false);

        // The audio master clock owns a device + feed loop; the software clock owns nothing. Dispose whichever
        // we were given if it is disposable, so the whole playback session tears down through one call.
        if (_clock is IAsyncDisposable asyncClock)
            await asyncClock.DisposeAsync().ConfigureAwait(false);
        else if (_clock is IDisposable syncClock)
            syncClock.Dispose();
    }
}
