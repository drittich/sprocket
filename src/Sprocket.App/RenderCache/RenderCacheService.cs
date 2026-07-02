using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Persistence;

namespace Sprocket.App.RenderCache;

/// <summary>A render the UI is about to run: the content hash the range resolves to right now, and the
/// intermediate file it will produce. Committed via <see cref="RenderCacheService.Commit"/> once rendered.</summary>
public readonly record struct PendingRender(
    RenderCacheScope Scope,
    Guid SequenceId,
    long InTicks,
    long OutTicks,
    string Hash,
    string FileName,
    int SampleRate,
    int Channels);

/// <summary>
/// The session's preview render cache (ARCHITECTURE.md §20, PLAN.md step 32): tracks which ranges of which
/// sequences have a rendered intermediate, whether each is still <b>valid</b> (its content hash of the range's
/// serializable state still matches — recomputed via <see cref="Refresh"/> after every model change, so
/// invalidation is exact and an <em>undo</em> re-validates without re-rendering), and exposes the valid ranges
/// back to playback through the Core seams: <see cref="IVideoRenderCache"/> for the engine's cached-segment
/// player and <see cref="IAudioRenderCache"/> for the audio feeder. Export never consults this service (§17).
/// </summary>
/// <remarks>All state mutation (<see cref="Refresh"/>, <see cref="Commit"/>, <see cref="DeleteAll"/>) happens on
/// the UI thread; the playback pump and audio feeder read immutable snapshot arrays, so lookups are lock-free.
/// The service is per-session, like <c>ProxyService</c>, and owns nothing the project needs — the cache is a
/// local, discardable derived artifact.</remarks>
public sealed class RenderCacheService : IVideoRenderCache, IAudioRenderCache, IDisposable
{
    private readonly Project _project;
    private readonly RenderCacheStore _store;
    private readonly List<Entry> _entries; // UI thread only

    private volatile CachedRenderSegment[] _videoSnapshot = [];
    private volatile AudioSegment[] _audioSnapshot = [];
    private bool _disposed;

    private sealed class Entry
    {
        public required RenderSegmentRecord Record { get; init; }

        /// <summary>The synthetic source id playback keys the segment's feed on — regenerated per session
        /// (never persisted; the cache is identified by content hash, not by id).</summary>
        public required MediaRefId CacheId { get; init; }

        public bool Valid { get; set; }
    }

    /// <summary>Creates the service for <paramref name="project"/>, loading any manifest the project's cache
    /// directory (derived from <paramref name="projectFilePath"/>) already holds and validating it against the
    /// current model — a reopened project's still-matching renders are valid immediately, no re-render.</summary>
    public RenderCacheService(Project project, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _store = new RenderCacheStore(projectFilePath);
        _entries = [.. _store.Load().Select(r => new Entry { Record = r, CacheId = MediaRefId.New() })];
        Refresh();
    }

    /// <summary>The cache directory (for the Delete Render Files confirmation).</summary>
    public string Directory => _store.Directory;

    /// <summary>Total bytes the project's render cache occupies on disk right now.</summary>
    public long SizeBytes() => _store.SizeBytes();

    /// <summary>
    /// Re-validates every segment against the current model — each segment's range re-hashes and is valid only
    /// when the hash it was rendered under still matches (§20 exact invalidation) — and rebuilds the lock-free
    /// snapshots playback reads (restricted to the <em>active</em> sequence). Call after any model change (the
    /// command stack's <c>Changed</c>) and after switching the active sequence. UI thread.
    /// </summary>
    public void Refresh()
    {
        foreach (Entry entry in _entries)
            entry.Valid = IsStillValid(entry.Record);
        RebuildSnapshots();
    }

    private bool IsStillValid(RenderSegmentRecord record)
    {
        var sequenceId = new SequenceId(record.SequenceId);
        if (_project.GetSequence(sequenceId) is null)
            return false;
        try
        {
            RenderCacheScope scope = record.Kind == "audio" ? RenderCacheScope.Audio : RenderCacheScope.Video;
            return RenderCacheHasher.ComputeHash(
                _project, sequenceId, new Timecode(record.InTicks), new Timecode(record.OutTicks), scope) == record.Hash;
        }
        catch
        {
            return false; // a hash failure only ever means "render again" (§15)
        }
    }

    private void RebuildSnapshots()
    {
        Guid active = _project.ActiveSequence.Id.Value;

        var video = new List<CachedRenderSegment>();
        var audio = new List<AudioSegment>();
        AudioSegment[] oldAudio = _audioSnapshot;
        foreach (Entry entry in _entries)
        {
            if (!entry.Valid || entry.Record.SequenceId != active)
                continue;
            if (entry.Record.Kind == "audio")
            {
                // Reuse the existing wrapper (and its open reader) when the same file stays valid across a refresh.
                AudioSegment? existing = Array.Find(oldAudio, s => s.FileName == entry.Record.FileName);
                audio.Add(existing ?? new AudioSegment(entry.Record, _store.PathFor(entry.Record.FileName)));
            }
            else
            {
                video.Add(new CachedRenderSegment(
                    entry.CacheId, new Timecode(entry.Record.InTicks), new Timecode(entry.Record.OutTicks),
                    _store.PathFor(entry.Record.FileName)));
            }
        }

        _videoSnapshot = [.. video];
        AudioSegment[] newAudio = [.. audio];
        _audioSnapshot = newAudio;

        // Release readers for segments that dropped out (their files may be about to be deleted/re-rendered).
        foreach (AudioSegment old in oldAudio)
        {
            if (Array.IndexOf(newAudio, old) < 0)
                old.Dispose();
        }
    }

    /// <summary>The active sequence's segments (both scopes, valid and dirty) for the render bar. UI thread.</summary>
    public IReadOnlyList<(Timecode In, Timecode Out, RenderCacheScope Scope, bool Valid)> SegmentsForActiveSequence()
    {
        Guid active = _project.ActiveSequence.Id.Value;
        var list = new List<(Timecode, Timecode, RenderCacheScope, bool)>();
        foreach (Entry entry in _entries)
        {
            if (entry.Record.SequenceId != active)
                continue;
            RenderCacheScope scope = entry.Record.Kind == "audio" ? RenderCacheScope.Audio : RenderCacheScope.Video;
            list.Add((new Timecode(entry.Record.InTicks), new Timecode(entry.Record.OutTicks), scope, entry.Valid));
        }
        return list;
    }

    /// <summary>
    /// Prepares a render of <paramref name="scope"/> over [<paramref name="rangeIn"/>, <paramref name="rangeOut"/>)
    /// of sequence <paramref name="sequenceId"/>: computes the content hash the range resolves to right now and the
    /// file it will produce. UI thread (the hash reads the model).
    /// </summary>
    public PendingRender Prepare(
        RenderCacheScope scope, SequenceId sequenceId, Timecode rangeIn, Timecode rangeOut,
        int sampleRate = 0, int channels = 0)
    {
        string hash = RenderCacheHasher.ComputeHash(_project, sequenceId, rangeIn, rangeOut, scope);
        string kind = scope == RenderCacheScope.Audio ? "audio" : "video";
        string extension = scope == RenderCacheScope.Audio ? PreviewRenderer.AudioExtension : PreviewRenderer.VideoExtension;
        string fileName = RenderCacheStore.FileNameFor(kind, rangeIn.Ticks, rangeOut.Ticks, hash, extension);
        return new PendingRender(scope, sequenceId.Value, rangeIn.Ticks, rangeOut.Ticks, hash, fileName, sampleRate, channels);
    }

    /// <summary>The absolute output path for a prepared render.</summary>
    public string FilePathFor(in PendingRender pending) => _store.PathFor(pending.FileName);

    /// <summary>Whether an identical, still-valid segment already exists (same scope/range/hash with its file on
    /// disk) — the render command then has nothing to do.</summary>
    public bool IsAlreadyRendered(in PendingRender pending)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Valid
                && entry.Record.Hash == pending.Hash
                && entry.Record.SequenceId == pending.SequenceId
                && entry.Record.InTicks == pending.InTicks
                && entry.Record.OutTicks == pending.OutTicks
                && entry.Record.Kind == (pending.Scope == RenderCacheScope.Audio ? "audio" : "video"))
                return System.IO.File.Exists(_store.PathFor(entry.Record.FileName));
        }
        return false;
    }

    /// <summary>
    /// Records a completed render: supersedes any same-scope segments of the sequence fully contained in the new
    /// range (partial overlaps are kept — the newest covering segment wins at lookup), persists the manifest,
    /// sweeps orphaned files, and refreshes the snapshots so playback replays the render immediately. UI thread.
    /// </summary>
    public void Commit(in PendingRender pendingRender)
    {
        PendingRender pending = pendingRender; // lambda-capturable copy
        string kind = pending.Scope == RenderCacheScope.Audio ? "audio" : "video";
        _entries.RemoveAll(e =>
            e.Record.Kind == kind
            && e.Record.SequenceId == pending.SequenceId
            && e.Record.InTicks >= pending.InTicks
            && e.Record.OutTicks <= pending.OutTicks);
        _entries.Add(new Entry
        {
            Record = new RenderSegmentRecord(
                kind, pending.SequenceId, pending.InTicks, pending.OutTicks, pending.Hash, pending.FileName,
                pending.SampleRate, pending.Channels),
            CacheId = MediaRefId.New(),
        });
        SaveAndSweep();
        Refresh();
    }

    /// <summary>Delete Render Files: forgets every segment and deletes the cache directory's files (best-effort —
    /// a file still held by a winding-down decoder is swept next time). Returns the bytes reclaimed.</summary>
    public long DeleteAll()
    {
        _entries.Clear();
        RebuildSnapshots(); // playback stops resolving segments before the files go away
        _store.Save([]);
        return _store.DeleteAll();
    }

    private void SaveAndSweep()
    {
        List<RenderSegmentRecord> records = [.. _entries.Select(e => e.Record)];
        _store.Save(records);
        _store.DeleteOrphans(records);
    }

    // ---- playback seams (any thread: snapshot reads only) ----

    /// <inheritdoc />
    public CachedRenderSegment? ResolveAt(Timecode position)
    {
        CachedRenderSegment[] snapshot = _videoSnapshot;
        for (int i = snapshot.Length - 1; i >= 0; i--) // newest-last: the most recent covering render wins
        {
            if (snapshot[i].Contains(position))
                return snapshot[i];
        }
        return null;
    }

    /// <inheritdoc />
    public bool TryRead(Timecode start, Span<float> interleaved)
    {
        AudioSegment[] snapshot = _audioSnapshot;
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].TryRead(start, interleaved))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AudioSegment[] snapshot = _audioSnapshot;
        _audioSnapshot = [];
        _videoSnapshot = [];
        foreach (AudioSegment segment in snapshot)
            segment.Dispose();
    }

    /// <summary>One valid cached-audio range: a lazily-opened <see cref="WavePcmReader"/> (the §20 cache
    /// <c>IPcmReader</c>) guarded by a gate so the feeder's reads and the UI's disposal can't interleave.</summary>
    private sealed class AudioSegment(RenderSegmentRecord record, string filePath) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Timecode _start = new(record.InTicks);
        private readonly Timecode _end = new(record.OutTicks);
        private readonly int _sampleRate = record.SampleRate;
        private readonly int _channels = record.Channels;
        private WavePcmReader? _reader;
        private bool _dead;

        public string FileName { get; } = record.FileName;

        public bool TryRead(Timecode start, Span<float> interleaved)
        {
            if (_sampleRate <= 0 || _channels <= 0 || interleaved.Length % _channels != 0)
                return false;
            int frames = interleaved.Length / _channels;
            Timecode end = start + Timecode.FromSamples(frames, _sampleRate);
            if (start < _start || end > _end)
                return false; // must cover the whole buffer — no seams inside one buffer

            lock (_gate)
            {
                if (_dead)
                    return false;
                try
                {
                    _reader ??= WavePcmReader.Open(filePath);
                    if (_reader.SampleRate != _sampleRate || _reader.Channels != _channels)
                    {
                        MarkDeadLocked();
                        return false;
                    }
                    _reader.SeekTo(start - _start);
                    return _reader.Read(interleaved) == frames;
                }
                catch
                {
                    MarkDeadLocked(); // unreadable/truncated file → mix live from now on (§15)
                    return false;
                }
            }
        }

        private void MarkDeadLocked()
        {
            _dead = true;
            _reader?.Dispose();
            _reader = null;
        }

        public void Dispose()
        {
            lock (_gate)
                MarkDeadLocked();
        }
    }
}
