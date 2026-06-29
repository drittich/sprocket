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
    ResolvedGenerator? Generator = null);

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
    IReadOnlyList<ResolvedEffect> Effects);

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
    private readonly int _paceMsPlaying;

    private readonly Func<MediaRefId, IVideoFrameFeed?>? _feedFactory;
    private readonly bool _reconcile;

    private readonly object _frameGate = new();
    private readonly List<VideoTrackPlayer> _players = [];

    private readonly object _invalidateGate = new();
    private readonly HashSet<MediaRefId> _invalidatedSources = [];

    private readonly object _transportGate = new();
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _endHandled;

    private long _seekGeneration;     // bumped by SeekTo; the pump re-seeks players on change
    private long _lastPumpGen;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private bool _started;
    private bool _disposed;

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

        _paceMsPlaying = ComputePace(project);
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
        _paceMsPlaying = ComputePace(project);
    }

    private static int ComputePace(Project project)
    {
        Rational fps = project.Timeline.FrameRate;
        double frameMs = fps.Num > 0 ? 1000.0 * fps.Den / fps.Num : 1000.0 / 30;
        return Math.Clamp((int)(frameMs / 2), 4, 33);
    }

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

                    default:
                        if (FindPlayer(track)?.Current is { } frame)
                            layers.Add(new PresentedVideoLayer(
                                frame.Pixels, frame.RowBytes, frame.Width, frame.Height, frame.Pts,
                                effects, track.Opacity, track.BlendMode));
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
                top = new PresentedFrame(frame.Pixels, frame.RowBytes, frame.Width, frame.Height, frame.Pts, effects);
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
                int pace = State == PlaybackState.Playing ? _paceMsPlaying : 16;
                await Task.Delay(pace, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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
        }
        bool force = forcePresent || seekChanged;

        if (_reconcile)
            await ReconcilePlayersAsync().ConfigureAwait(false);

        ApplyInvalidations();

        Timecode pos = PlaybackMath.ClampToTimeline(_clock.Now, Duration);

        bool promoted = false;
        foreach (VideoTrackPlayer player in _players)
            promoted |= await player.PumpAsync(pos, force, ct).ConfigureAwait(false);

        // Generator / adjustment clips have no decoder to "promote" a frame, so they'd never trigger a repaint.
        // Repaint for them when playing (their content/effects may animate) or when a seek forces a present
        // (a scrub onto a title). A static synthetic clip while paused doesn't repaint every idle tick.
        bool synthetic = (State == PlaybackState.Playing || force) && HasActiveSyntheticVideoClip(pos);
        if (promoted || synthetic)
            FramePresented?.Invoke();

        PositionChanged?.Invoke(pos);
        HandleEnd(pos);
    }

    /// <summary>Whether any enabled video track has a generator or adjustment clip active at <paramref name="pos"/>.</summary>
    private bool HasActiveSyntheticVideoClip(Timecode pos)
    {
        foreach (VideoTrack track in _project.Timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;
            if (track.ResolveActiveClip(pos) is { Kind: ClipKind.Generator or ClipKind.Adjustment })
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
        bool changed;
        lock (_transportGate)
        {
            changed = _state != state;
            _state = state;
        }
        if (changed)
            StateChanged?.Invoke(state);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _pumpCts?.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        foreach (VideoTrackPlayer player in _players)
            await player.DisposeAsync().ConfigureAwait(false);
        _players.Clear();

        // The audio master clock owns a device + feed loop; the software clock owns nothing. Dispose whichever
        // we were given if it is disposable, so the whole playback session tears down through one call.
        if (_clock is IAsyncDisposable asyncClock)
            await asyncClock.DisposeAsync().ConfigureAwait(false);
        else if (_clock is IDisposable syncClock)
            syncClock.Dispose();

        _pumpCts?.Dispose();
    }
}
