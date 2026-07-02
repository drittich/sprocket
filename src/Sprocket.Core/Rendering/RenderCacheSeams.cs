using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Rendering;

/// <summary>
/// One valid pre-rendered range of the active sequence's composited video output (ARCHITECTURE.md §20,
/// PLAN.md step 32): a fast all-intra intermediate on disk whose frame at source time <c>t - Start</c> is the
/// sequence's composited frame at timeline time <c>t</c>. Exposed to playback as just another decodable source —
/// the same seam media and proxies use — so the engine replays the cache instead of recomputing the subgraph.
/// </summary>
/// <param name="CacheId">A synthetic, content-addressed source id for this segment (never in the media pool);
/// stable for the segment's life, so a feed keyed on it is rebuilt exactly when the segment changes.</param>
/// <param name="Start">The segment's timeline in-point (inclusive).</param>
/// <param name="End">The segment's timeline out-point (exclusive).</param>
/// <param name="FilePath">The rendered intermediate on disk (local, regenerable, safely discardable).</param>
public readonly record struct CachedRenderSegment(MediaRefId CacheId, Timecode Start, Timecode End, string FilePath)
{
    /// <summary>Whether <paramref name="position"/> falls inside this segment's half-open range.</summary>
    public bool Contains(Timecode position) => position >= Start && position < End;
}

/// <summary>
/// The video render cache seam (ARCHITECTURE.md §20): resolves a timeline position to the valid pre-rendered
/// segment covering it, or <see langword="null"/> when that position must be composited live. The render graph
/// itself never learns about caching — only the preview engine consults this, and export ignores it entirely
/// (§17: export re-renders full-resolution originals). Implementations must be safe to call from any thread
/// (the playback pump and the UI query it concurrently).
/// </summary>
public interface IVideoRenderCache
{
    /// <summary>The valid cached segment covering <paramref name="position"/>, or <see langword="null"/>.</summary>
    CachedRenderSegment? ResolveAt(Timecode position);
}

/// <summary>
/// The audio render cache seam (ARCHITECTURE.md §20, PLAN.md step 32): cached master-mix PCM for pre-rendered
/// ranges ("Render Audio" / freezing, the seam step 41's heavy reverb tails consume). The audio engine's feeder
/// consults it before mixing; export ignores it (§17). Implementations must be callable from the audio feeder
/// thread without blocking on locks held across mixing.
/// </summary>
public interface IAudioRenderCache
{
    /// <summary>
    /// Fills <paramref name="interleaved"/> with cached master-mix samples starting at timeline time
    /// <paramref name="start"/>, returning <see langword="true"/> only when the whole span is covered by a single
    /// valid cached range (a partially-covered or invalid span returns <see langword="false"/> and the caller
    /// mixes live — no stale audio, no seams inside one buffer).
    /// </summary>
    bool TryRead(Timecode start, Span<float> interleaved);
}
