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
    private bool _feedStarted;
    private bool _needsSeek = true;    // a fresh player (or one after a seek) must seek before presenting
    private bool _atEof;               // the feed reached end-of-stream; hold (don't re-read) until a seek resumes it

    private VideoFrame? _current;      // presented frame; guarded by _frameGate
    private VideoFrame? _next;         // pump-thread-only prefetch

    /// <summary>Fixed-feed player (slice/test path): always decodes <paramref name="feed"/>, source never changes.</summary>
    public VideoTrackPlayer(VideoTrack track, IVideoFrameFeed feed, object frameGate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(frameGate);
        Track = track;
        _feed = feed;
        _frameGate = frameGate;
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

    /// <summary>Forces the next pump to re-seek the feed (called by the engine when the playhead jumps).</summary>
    public void MarkNeedsSeek() => _needsSeek = true;

    /// <summary>
    /// Advances this track's frame toward the playhead <paramref name="pos"/>: (re)targets the feed to the
    /// active clip's source, seeks if needed, then promotes the latest frame at/just before the target while
    /// dropping intermediates. Returns whether the presented frame changed. With no active clip (or an
    /// offline source) the track contributes nothing and its current frame is cleared.
    /// </summary>
    public async Task<bool> PumpAsync(Timecode pos, bool force, CancellationToken ct)
    {
        Clip? clip = Track.Enabled ? Track.ResolveActiveClip(pos) : null;
        if (clip is null)
        {
            ClearCurrent();
            return false;
        }

        if (!EnsureFeedFor(clip.MediaRefId))
        {
            ClearCurrent();
            return false;
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
            return false;

        bool promoted = false;
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
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // A null prefetch is end-of-stream: latch it so the next pump holds rather than blocking on a read the
        // parked worker can't satisfy until a seek. A seek (above) clears the latch and resumes decoding.
        if (_next is null)
            _atEof = true;

        return promoted;
    }

    /// <summary>Ensures <see cref="_feed"/> decodes <paramref name="sourceId"/>, (re)building it in factory mode
    /// when the source changes. Returns false when no feed is available (offline / no-video source).</summary>
    private bool EnsureFeedFor(MediaRefId sourceId)
    {
        if (_feedFactory is null)
            return _feed is not null; // fixed feed: source assumed constant

        if (_feed is not null && _feedSource == sourceId)
            return true;

        // The active clip's source changed: tear down the old feed and build one for the new source.
        DisposeFeed();
        _feed = _feedFactory(sourceId);
        _feedSource = sourceId;
        _feedStarted = false;
        _needsSeek = true;
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
