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
/// A snapshot of the currently-presented frame's pixels, valid only for the duration of the
/// <see cref="PlaybackEngine.UseCurrentFrame"/> callback (the engine holds the frame's lock during it).
/// The pixels live in the decoder's native buffer (no managed copy, ARCHITECTURE.md §1).
/// </summary>
/// <param name="Pixels">Pointer to the RGBA8888 pixels.</param>
/// <param name="RowBytes">Stride in bytes.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Pts">The frame's source presentation time.</param>
/// <param name="Effects">The active clip's effect chain, evaluated at the current playhead time (bottom→top);
/// empty when the clip has none. The Render layer turns these into the SkSL shader graph (PLAN.md step 7).</param>
public readonly record struct PresentedFrame(
    nint Pixels,
    int RowBytes,
    int Width,
    int Height,
    Timecode Pts,
    IReadOnlyList<ResolvedEffect> Effects);

/// <summary>
/// The slice's playback engine (PLAN.md step 4): drives a single video track from a master
/// <see cref="IClock"/>, keeping the presented frame in sync by dropping or holding decoded frames
/// (ARCHITECTURE.md §8). Transport (<see cref="Play"/>/<see cref="Pause"/>/<see cref="SeekTo"/>) is callable
/// from the UI thread; a background pump consumes the <see cref="IVideoFrameFeed"/> and updates the presented
/// frame. Audio and multi-track compositing are deferred (PLAN steps 5/14) — this is video-only.
/// </summary>
/// <remarks>
/// <para><b>Events</b> (<see cref="FramePresented"/>, <see cref="PositionChanged"/>, <see cref="StateChanged"/>,
/// <see cref="PlaybackEnded"/>) are raised on the background pump thread; UI subscribers must marshal to their
/// own thread. <see cref="UseCurrentFrame"/> is the safe way to read the live frame: it holds the frame lock
/// for the callback so the pump cannot recycle the buffer mid-draw.</para>
/// <para>For the slice the engine plays the first enabled video track and assumes one source/feed. Multiple
/// clips/sources and audio-master sync slot onto the same pump without redesign.</para>
/// </remarks>
public sealed class PlaybackEngine : IAsyncDisposable
{
    private readonly Project _project;
    private readonly VideoTrack? _videoTrack;
    private readonly IVideoFrameFeed _feed;
    private readonly IMasterClock _clock;
    private readonly int _paceMsPlaying;

    private readonly object _frameGate = new();
    private VideoFrame? _current;     // presented frame; guarded by _frameGate
    private VideoFrame? _next;        // pump-thread-only prefetch (one frame of read-ahead)

    private readonly object _transportGate = new();
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _endHandled;

    private long _seekGeneration;     // bumped by SeekTo; the pump drops its stale prefetch on change
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates an engine over <paramref name="project"/>, playing its first enabled video track from
    /// <paramref name="feed"/>. <paramref name="clock"/> is the master clock (audio device clock when audio is
    /// present, ARCHITECTURE.md §8); it defaults to a fresh <see cref="SoftwareClock"/> for the video-only case.
    /// If the clock is <see cref="IAsyncDisposable"/> the engine takes ownership and disposes it on teardown.</summary>
    public PlaybackEngine(Project project, IVideoFrameFeed feed, IMasterClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(feed);

        _project = project;
        _feed = feed;
        _clock = clock ?? new SoftwareClock();
        _videoTrack = project.Timeline.VideoTracks.FirstOrDefault(t => t.Enabled)
                      ?? project.Timeline.VideoTracks.FirstOrDefault();

        Rational fps = project.Timeline.FrameRate;
        double frameMs = fps.Num > 0 ? 1000.0 * fps.Den / fps.Num : 1000.0 / 30;
        _paceMsPlaying = Math.Clamp((int)(frameMs / 2), 4, 33);
    }

    /// <summary>Raised after the presented frame changes. Fires on the pump thread.</summary>
    public event Action? FramePresented;

    /// <summary>Raised each pump tick with the current timeline position. Fires on the pump thread.</summary>
    public event Action<Timecode>? PositionChanged;

    /// <summary>Raised when the transport state changes. Fires on the pump or calling thread.</summary>
    public event Action<PlaybackState>? StateChanged;

    /// <summary>Raised once when playback reaches the end of the timeline. Fires on the pump thread.</summary>
    public event Action? PlaybackEnded;

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
    /// Starts the feed and the background pump and positions the playhead at the start, so the first frame is
    /// presented before play begins. Idempotent.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _started = true;

        _feed.Start();
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
        SeekTo(Timecode.Zero); // position the feed at the active clip's in-point and load frame 0
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
    /// Moves the playhead to <paramref name="position"/> (clamped to the timeline), seeking the feed so the
    /// presented frame updates to match. Keeps the running/paused state. Safe to call while playing (scrub).
    /// </summary>
    public void SeekTo(Timecode position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Timecode clamped = PlaybackMath.ClampToTimeline(position, Duration);

        _clock.Seek(clamped);
        Clip? clip = _videoTrack?.ResolveActiveClip(clamped);
        if (clip is not null)
            _feed.RequestSeek(clip.MapToSource(clamped));

        // The pump sees the bumped generation, drops its stale prefetch, and force-presents the post-seek frame.
        Interlocked.Increment(ref _seekGeneration);

        lock (_transportGate)
            _endHandled = false;
    }

    /// <summary>
    /// Invokes <paramref name="use"/> with the currently-presented frame (or <c>null</c> if none yet), holding
    /// the frame lock for the duration so the pump cannot recycle the native buffer. Keep the callback short
    /// (it runs under a lock contended by the pump) and do not retain the <see cref="PresentedFrame"/> beyond it.
    /// </summary>
    public void UseCurrentFrame(Action<PresentedFrame?> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        lock (_frameGate)
        {
            PresentedFrame? frame = _current is null
                ? null
                : new PresentedFrame(
                    _current.Pixels, _current.RowBytes, _current.Width, _current.Height, _current.Pts,
                    ResolveCurrentEffects());
            use(frame);
        }
    }

    /// <summary>
    /// Resolves the effect chain for the clip under the playhead at the current position, evaluated at that
    /// time so animated parameters (the fade ramp) track the live position. Empty when no clip/effects.
    /// </summary>
    private IReadOnlyList<ResolvedEffect> ResolveCurrentEffects()
    {
        Clip? clip = _videoTrack?.ResolveActiveClip(Position);
        return clip is null ? [] : RenderGraph.ResolveEffects(clip, Position);
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        long lastSeekGen = Interlocked.Read(ref _seekGeneration);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool forcePresent = false;
                long gen = Interlocked.Read(ref _seekGeneration);
                if (gen != lastSeekGen)
                {
                    lastSeekGen = gen;
                    _next?.Dispose();   // prefetch belongs to a superseded position
                    _next = null;
                    forcePresent = true;
                }

                await PumpOnceAsync(forcePresent, ct).ConfigureAwait(false);

                int pace = State == PlaybackState.Playing ? _paceMsPlaying : 16;
                await Task.Delay(pace, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One pump iteration: catch the presented frame up to the playhead, then report position/end.
    /// Factored out so deterministic tests can step the pump without the real-time delay loop.</summary>
    internal async Task PumpOnceAsync(bool forcePresent, CancellationToken ct)
    {
        Timecode pos = PlaybackMath.ClampToTimeline(_clock.Now, Duration);
        Clip? clip = _videoTrack?.ResolveActiveClip(pos);

        bool promoted = false;
        if (clip is not null)
        {
            Timecode target = clip.MapToSource(pos);

            _next ??= await _feed.ReadAsync(ct).ConfigureAwait(false);

            // After a seek, present the freshly decoded frame even if its PTS sits just past the target.
            if (forcePresent && _next is not null)
            {
                Promote(_next);
                promoted = true;
                _next = await _feed.ReadAsync(ct).ConfigureAwait(false);
            }

            // Advance through every frame already due, dropping intermediates, landing on the latest ≤ target.
            while (_next is not null && PlaybackMath.ShouldPromote(_next.Pts, target, forcePresent: false))
            {
                Promote(_next);
                promoted = true;
                _next = await _feed.ReadAsync(ct).ConfigureAwait(false);
            }
        }

        if (promoted)
            FramePresented?.Invoke();

        PositionChanged?.Invoke(pos);
        HandleEnd(pos);
    }

    /// <summary>Swaps <paramref name="frame"/> in as the presented frame under the lock and recycles the old one.</summary>
    private void Promote(VideoFrame frame)
    {
        VideoFrame? old;
        lock (_frameGate)
        {
            old = _current;
            _current = frame;
        }
        old?.Dispose();
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

        _clock.Pause();
        _clock.Seek(Duration);
        SetState(PlaybackState.Stopped);
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

        await _feed.DisposeAsync().ConfigureAwait(false);

        // The audio master clock owns a device + feed loop; the software clock owns nothing. Dispose whichever
        // we were given if it is disposable, so the whole playback session tears down through one call.
        if (_clock is IAsyncDisposable asyncClock)
            await asyncClock.DisposeAsync().ConfigureAwait(false);
        else if (_clock is IDisposable syncClock)
            syncClock.Dispose();

        lock (_frameGate)
        {
            _current?.Dispose();
            _current = null;
        }
        _next?.Dispose();
        _next = null;
        _pumpCts?.Dispose();
    }
}
