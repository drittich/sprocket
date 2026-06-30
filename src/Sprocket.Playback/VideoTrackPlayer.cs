using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Playback;

/// <summary>
/// Decodes and presents the frames for one <see cref="VideoTrack"/> on behalf of the <see cref="PlaybackEngine"/>
/// (PLAN.md step 14). It owns the track's <see cref="IVideoFrameFeed"/> and keeps one presented frame plus a
/// one-frame prefetch, dropping/holding frames to stay in sync with the playhead — the same per-track logic the
/// slice's single-track engine used, now one instance per track so the engine can composite N video tracks.
/// </summary>
/// <remarks>
/// Two feed-binding modes: a <b>fixed</b> feed (the slice/test path — one supplied feed, source never changes)
/// or a <b>factory</b> (the app path — the feed is created lazily for the active clip's source and rebuilt when
/// the active clip's source changes). All pumping happens on the engine's pump thread; the presented frame is
/// swapped under the engine's frame gate so the UI draw can never see a recycled buffer (ARCHITECTURE.md §1/§8).
/// </remarks>
internal sealed class VideoTrackPlayer : IAsyncDisposable
{
    private readonly Func<MediaRefId, IVideoFrameFeed?>? _feedFactory;
    private readonly object _frameGate;

    private IVideoFrameFeed? _feed;
    private MediaRefId? _feedSource;   // which source _feed currently decodes (factory mode)
    private Clip? _feedClip;           // which clip the feed is currently positioned for (so a same-source clip change re-seeks)
    private bool _feedStarted;
    private bool _needsSeek = true;    // a fresh player (or one after a seek) must seek before presenting
    private bool _atEof;               // the feed reached end-of-stream; hold (don't re-read) until a seek resumes it
    private bool _needsRebuild;        // the source's best-available file changed (e.g. a proxy became ready)

    private VideoFrame? _current;      // presented frame; guarded by _frameGate
    private VideoFrame? _next;         // pump-thread-only prefetch

    // Decode info (codec + hw device) of the current feed, snapshotted when the feed is (re)built — it is
    // immutable for a feed's life, so caching it lets the diagnostics overlay read it from the UI thread
    // without touching native decoder state. Set on the pump thread / at construction; read cross-thread.
    private VideoDecodeInfo? _decodeInfo;

    /// <summary>Fixed-feed player (slice/test path): always decodes <paramref name="feed"/>, source never changes.</summary>
    public VideoTrackPlayer(VideoTrack track, IVideoFrameFeed feed, object frameGate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(frameGate);
        Track = track;
        _feed = feed;
        _frameGate = frameGate;
        _decodeInfo = feed.DecodeInfo;
    }

    /// <summary>Factory player (app path): creates the feed lazily for the active clip's source.</summary>
    public VideoTrackPlayer(VideoTrack track, Func<MediaRefId, IVideoFrameFeed?> feedFactory, object frameGate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(feedFactory);
        ArgumentNullException.ThrowIfNull(frameGate);
        Track = track;
        _feedFactory = feedFactory;
        _frameGate = frameGate;
    }

    /// <summary>The track this player renders.</summary>
    public VideoTrack Track { get; }

    /// <summary>The currently-presented frame, or <c>null</c>. Read only while holding the engine's frame gate.</summary>
    public VideoFrame? Current => _current;

    /// <summary>How this player's current feed decodes (codec + hardware device), or <c>null</c> when it has no
    /// feed. A cached snapshot, safe to read from another thread (the diagnostics overlay).</summary>
    public VideoDecodeInfo? DecodeInfo => _decodeInfo;

    /// <summary>Forces the next pump to re-seek the feed (called by the engine when the playhead jumps).</summary>
    public void MarkNeedsSeek() => _needsSeek = true;

    /// <summary>Which source this player's feed currently decodes (factory mode), or <c>null</c> if it has no
    /// feed yet. Read on the pump thread.</summary>
    public MediaRefId? FeedSource => _feedSource;

    /// <summary>
    /// Requests that this player rebuild its feed on the next pump — used when a source's best-available file
    /// changes underneath it (a proxy became ready, PLAN.md step 18), so the preview transparently switches
    /// without a clip-source change. No-op in fixed-feed mode (the slice/test single feed). Called on the pump thread.
    /// </summary>
    public void RequestRebuild()
    {
        if (_feedFactory is not null)
            _needsRebuild = true;
    }

    /// <summary>
    /// Advances this track's frame toward the playhead <paramref name="pos"/>: (re)targets the feed to the
    /// active clip's source, seeks if needed, then promotes the latest frame at/just before the target while
    /// dropping intermediates. Returns whether the presented frame changed (<c>Promoted</c>) and how many decoded
    /// frames were skipped to catch up (<c>Dropped</c>, for the diagnostics overlay). With no active clip (or an
    /// offline source) the track contributes nothing and its current frame is cleared.
    /// </summary>
    public async Task<(bool Promoted, int Dropped)> PumpAsync(Timecode pos, bool force, CancellationToken ct)
    {
        Clip? clip = Track.Enabled ? Track.ResolveActiveClip(pos) : null;
        if (clip is null)
        {
            ClearCurrent();
            _feedClip = null; // re-entering any clip must re-seek the feed to that clip's in-point
            return (false, 0);
        }

        // A change of active clip breaks source-time continuity even when the next clip draws from the SAME
        // source — e.g. the same media placed twice on one track with a gap between the two clips. The feed is
        // left sitting at the previous clip's out-point (or parked at EOF), so it must seek to the new clip's
        // mapped in-point. Without this the reused feed is positioned past the new clip's source range and never
        // promotes a frame, so the track holds black. EnsureFeedFor only re-seeks when the source id changes, so
        // a same-source clip change would otherwise slip through.
        if (!ReferenceEquals(clip, _feedClip))
        {
            _needsSeek = true;
            _feedClip = clip;
        }

        if (!EnsureFeedFor(clip.MediaRefId))
        {
            ClearCurrent();
            return (false, 0);
        }

        if (!_feedStarted)
        {
            _feed!.Start(); // idempotent
            _feedStarted = true;
        }

        Timecode target = clip.MapToSource(pos);
        bool localForce = force || _needsSeek;
        if (_needsSeek)
        {
            _feed!.RequestSeek(target);
            _next?.Dispose();
            _next = null;
            _atEof = false; // a seek resumes the feed from the new target
            _needsSeek = false;
        }

        // The feed has been drained to end-of-stream. The decode worker parks at EOF and only resumes on a seek,
        // so reading again here would block forever. Hold the last presented frame instead. Without this, once the
        // source is exhausted while the playhead is still inside the clip's timeline span (the audio master clock
        // keeps the position just short of the end), the pump would hang on the read — freezing the playhead and
        // never reaching the end-of-timeline stop, while the independent audio clock kept running.
        if (_atEof)
            return (false, 0);

        bool promoted = false;
        int advancePromotes = 0; // frames the steady-state advance loop promoted this pump (>1 ⇒ we fell behind)
        _next ??= await _feed!.ReadAsync(ct).ConfigureAwait(false);

        // After a seek, present the freshly decoded frame even if its PTS sits just past the target.
        if (localForce && _next is not null)
        {
            Promote(_next);
            promoted = true;
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // Advance through every frame already due, dropping intermediates, landing on the latest ≤ target.
        while (_next is not null && PlaybackMath.ShouldPromote(_next.Pts, target, forcePresent: false))
        {
            Promote(_next);
            promoted = true;
            advancePromotes++;
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // A null prefetch is end-of-stream: latch it so the next pump holds rather than blocking on a read the
        // parked worker can't satisfy until a seek. A seek (above) clears the latch and resumes decoding.
        if (_next is null)
            _atEof = true;

        // Diagnostics (playback-stats overlay): in steady-state playback the advance loop should promote exactly
        // one frame per due interval, so any promotions beyond the first mean the pump fell behind the clock and
        // skipped the intermediate frames — those are dropped frames (ARCHITECTURE.md §8). A forced post-seek/scrub
        // present is expected catch-up, not a stutter, so its skips are never counted as drops.
        int dropped = localForce ? 0 : Math.Max(0, advancePromotes - 1);
        return (promoted, dropped);
    }

    /// <summary>Ensures <see cref="_feed"/> decodes <paramref name="sourceId"/>, (re)building it in factory mode
    /// when the source changes. Returns false when no feed is available (offline / no-video source).</summary>
    private bool EnsureFeedFor(MediaRefId sourceId)
    {
        if (_feedFactory is null)
            return _feed is not null; // fixed feed: source assumed constant

        if (_feed is not null && _feedSource == sourceId && !_needsRebuild)
            return true;

        // Either the active clip's source changed, or the source's best-available file changed under us (a proxy
        // became ready). Tear down the old feed and build one for the (possibly same) source.
        DisposeFeed();
        _needsRebuild = false;
        _feed = _feedFactory(sourceId);
        _feedSource = sourceId;
        _feedStarted = false;
        _needsSeek = true;
        // Snapshot the (immutable) decode info now, before the feed's worker starts, so the overlay never reads
        // native decoder state off the pump thread.
        _decodeInfo = _feed?.DecodeInfo;
        return _feed is not null;
    }

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

    private void ClearCurrent()
    {
        VideoFrame? old;
        lock (_frameGate)
        {
            old = _current;
            _current = null;
        }
        old?.Dispose();
    }

    private void DisposeFeed()
    {
        _next?.Dispose();
        _next = null;
        if (_feed is not null)
        {
            // Fire-and-forget the async teardown of the old feed; we don't block the pump on it. A feed switch
            // only happens at a clip-source boundary, which is rare relative to the per-frame pump.
            IVideoFrameFeed old = _feed;
            _ = old.DisposeAsync();
            _feed = null;
        }
        _feedStarted = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_feed is not null)
            await _feed.DisposeAsync().ConfigureAwait(false);
        _feed = null;

        lock (_frameGate)
        {
            _current?.Dispose();
            _current = null;
        }
        _next?.Dispose();
        _next = null;
    }
}
