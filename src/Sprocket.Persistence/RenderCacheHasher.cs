using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Persistence;

/// <summary>Which half of the render graph a cache entry memoizes (ARCHITECTURE.md §20, PLAN.md step 32): the
/// composited video output, or the master audio mix. The two hash independently so an audio-only edit (a gain
/// ride, a bus EQ tweak) never invalidates a video render and vice versa.</summary>
public enum RenderCacheScope
{
    /// <summary>The sequence's composited video frames over the range.</summary>
    Video,

    /// <summary>The sequence's master audio mix over the range.</summary>
    Audio,
}

/// <summary>
/// Computes the content-hash key of a render-cache entry (ARCHITECTURE.md §20): a SHA-256 over the cached
/// subtree's <b>serializable state</b> — the same DTO shape that persists (§12), which is what makes the key
/// exact — restricted to what can affect the rendered output of the given range and scope. Because the render
/// graph is a pure function of (project, t), a cached range is valid exactly while this hash is unchanged: any
/// model edit that touches the range re-hashes differently (dirty), edits elsewhere don't (still valid), and
/// undoing an edit restores the old hash (valid again, no re-render).
/// </summary>
/// <remarks>
/// Included per scope: the sequence's render settings (resolution / frame rate / sample rate), the matching-kind
/// tracks' state with only the clips overlapping the range (and, for video, the transitions whose window overlaps),
/// the audio bus + project master chain (audio scope), every nested sequence / multicam source reachable from an
/// included clip (whole — a child edit anywhere invalidates conservatively), and the identity (path + size + mtime)
/// of every referenced media file, so a replaced source file invalidates even though the project JSON is unchanged.
/// </remarks>
public static class RenderCacheHasher
{
    /// <summary>Bumped when the hashed shape (or the rendered intermediate format) changes incompatibly, so stale
    /// caches from an older build simply read as dirty instead of replaying wrongly.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// The content hash (lowercase hex SHA-256) keying a cached render of <paramref name="scope"/> over the
    /// half-open range [<paramref name="rangeIn"/>, <paramref name="rangeOut"/>) of sequence
    /// <paramref name="sequenceId"/>. Throws <see cref="ArgumentException"/> when the sequence doesn't exist.
    /// </summary>
    public static string ComputeHash(
        Project project, SequenceId sequenceId, Timecode rangeIn, Timecode rangeOut, RenderCacheScope scope)
    {
        ArgumentNullException.ThrowIfNull(project);
        Sequence sequence = project.GetSequence(sequenceId)
            ?? throw new ArgumentException($"No sequence with id {sequenceId} in the project.", nameof(sequenceId));

        Timeline timeline = sequence.Timeline;
        TrackKind kind = scope == RenderCacheScope.Video ? TrackKind.Video : TrackKind.Audio;

        var mediaIds = new HashSet<MediaRefId>();
        var nestedIds = new List<SequenceId>();     // discovery order (deterministic); visited-set below dedupes
        var multicamIds = new List<MulticamId>();
        var visitedSequences = new HashSet<SequenceId>();
        var visitedMulticams = new HashSet<MulticamId>();

        // The sequence's own tracks, filtered to the range: only overlapping clips (and transitions whose window
        // overlaps) can affect the rendered output, so edits elsewhere on the timeline leave the hash unchanged.
        var tracks = new List<TrackDto>();
        foreach (Track track in timeline.Tracks)
        {
            bool wanted = (kind == TrackKind.Video && track is VideoTrack)
                          || (kind == TrackKind.Audio && track is AudioTrack);
            if (!wanted)
                continue;
            tracks.Add(FilterTrack(track, rangeIn, rangeOut, mediaIds, nestedIds, multicamIds,
                visitedSequences, visitedMulticams));
        }

        // Nested sequences reachable from an included clip hash whole (conservative: any child edit invalidates),
        // walking transitively — a grandchild edit must invalidate too. The queue grows as children are visited.
        var nested = new List<NestedTimelineDto>();
        for (int i = 0; i < nestedIds.Count; i++)
        {
            if (project.GetSequence(nestedIds[i]) is not { } child)
                continue; // dangling reference renders as nothing (§15) — nothing to hash
            nested.Add(new NestedTimelineDto(child.Id.Value, ProjectSerializer.ToDto(child.Timeline)));
            CollectReferences(child.Timeline, mediaIds, nestedIds, multicamIds, visitedSequences, visitedMulticams);
        }

        List<MulticamSourceDto>? multicams = null;
        if (multicamIds.Count > 0)
        {
            var sources = new List<MulticamSource>();
            foreach (MulticamId id in multicamIds)
            {
                MulticamSource? source = project.MulticamSources.Find(s => s.Id == id);
                if (source is null)
                    continue;
                sources.Add(source);
                foreach (MulticamAngle angle in source.Angles)
                {
                    mediaIds.Add(angle.MediaRefId);
                    if (angle.AudioMediaRefId is { } audioId)
                        mediaIds.Add(audioId);
                }
            }
            multicams = ProjectSerializer.ToMulticamList(sources);
        }

        var dto = new RenderHashDto(
            FormatVersion,
            scope.ToString(),
            new RationalDto(timeline.FrameRate.Num, timeline.FrameRate.Den),
            new ResolutionDto(timeline.Resolution.Width, timeline.Resolution.Height),
            timeline.SampleRate,
            tracks,
            BusEffects: scope == RenderCacheScope.Audio ? ProjectSerializer.ToEffectList(timeline.AudioEffects) : null,
            MasterGainDb: scope == RenderCacheScope.Audio ? project.Settings.MasterGainDb : null,
            MasterEffects: scope == RenderCacheScope.Audio
                ? ProjectSerializer.ToEffectList(project.Settings.MasterAudioEffects)
                : null,
            Nested: nested.Count > 0 ? nested : null,
            Multicams: multicams,
            Media: DescribeMedia(project, mediaIds));

        string json = JsonSerializer.Serialize(dto, SprocketJsonContext.Default.RenderHashDto);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>The track's DTO with only the content that can render inside [<paramref name="rangeIn"/>,
    /// <paramref name="rangeOut"/>): overlapping clips and overlapping-window transitions. Referenced media /
    /// nested-sequence / multicam ids are collected along the way.</summary>
    private static TrackDto FilterTrack(
        Track track, Timecode rangeIn, Timecode rangeOut,
        HashSet<MediaRefId> mediaIds, List<SequenceId> nestedIds, List<MulticamId> multicamIds,
        HashSet<SequenceId> visitedSequences, HashSet<MulticamId> visitedMulticams)
    {
        TrackDto dto = ProjectSerializer.ToDto(track);

        var clips = new List<ClipDto>();
        for (int i = 0; i < track.Clips.Count; i++)
        {
            Clip clip = track.Clips[i];
            if (clip.TimelineStart >= rangeOut || clip.TimelineEnd <= rangeIn)
                continue;
            clips.Add(dto.Clips[i]); // model order == DTO order (ToDto converts in place)
            CollectClipReferences(clip, mediaIds, nestedIds, multicamIds, visitedSequences, visitedMulticams);
        }

        List<TransitionDto>? transitions = null;
        if (dto.Transitions is { } allTransitions)
        {
            var overlapping = new List<TransitionDto>();
            for (int i = 0; i < track.Transitions.Count; i++)
            {
                Transition t = track.Transitions[i];
                if (t.Start < rangeOut && t.End > rangeIn)
                    overlapping.Add(allTransitions[i]);
            }
            transitions = overlapping.Count > 0 ? overlapping : null;
        }

        return dto with { Clips = clips, Transitions = transitions };
    }

    /// <summary>Collects every clip on <paramref name="timeline"/>'s tracks' references (used for nested
    /// sequences, which hash whole — their entire content affects the parent's render).</summary>
    private static void CollectReferences(
        Timeline timeline,
        HashSet<MediaRefId> mediaIds, List<SequenceId> nestedIds, List<MulticamId> multicamIds,
        HashSet<SequenceId> visitedSequences, HashSet<MulticamId> visitedMulticams)
    {
        foreach (Track track in timeline.Tracks)
            foreach (Clip clip in track.Clips)
                CollectClipReferences(clip, mediaIds, nestedIds, multicamIds, visitedSequences, visitedMulticams);
    }

    private static void CollectClipReferences(
        Clip clip,
        HashSet<MediaRefId> mediaIds, List<SequenceId> nestedIds, List<MulticamId> multicamIds,
        HashSet<SequenceId> visitedSequences, HashSet<MulticamId> visitedMulticams)
    {
        switch (clip.Kind)
        {
            case ClipKind.Sequence when clip.SourceSequenceId is { } childId:
                if (visitedSequences.Add(childId))
                    nestedIds.Add(childId);
                break;
            case ClipKind.Multicam when clip.SourceMulticamId is { } multicamId:
                if (visitedMulticams.Add(multicamId))
                    multicamIds.Add(multicamId);
                break;
            case ClipKind.Generator or ClipKind.Adjustment:
                break; // no source media
            default:
                mediaIds.Add(clip.MediaRefId);
                break;
        }
    }

    /// <summary>One identity line per referenced source — id, path, size, and mtime — sorted for determinism.
    /// A missing/offline file describes as zero size/mtime, so relinking or replacing the file invalidates.</summary>
    private static List<string> DescribeMedia(Project project, HashSet<MediaRefId> ids)
    {
        var lines = new List<string>(ids.Count);
        foreach (MediaRefId id in ids)
        {
            MediaRef? media = project.MediaPool.Get(id);
            string path = media?.AbsolutePath ?? string.Empty;
            long length = 0, mtime = 0;
            if (!string.IsNullOrEmpty(path))
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    length = info.Length;
                    mtime = info.LastWriteTimeUtc.Ticks;
                }
            }
            lines.Add($"{id.Value:N}|{path}|{length}|{mtime}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}

/// <summary>The canonical shape hashed by <see cref="RenderCacheHasher"/> — never written to disk, but kept a
/// source-generated DTO so the hash is deterministic and evolves with the persisted format it mirrors (§12).</summary>
internal sealed record RenderHashDto(
    int FormatVersion,
    string Scope,
    RationalDto FrameRate,
    ResolutionDto Resolution,
    int SampleRate,
    List<TrackDto> Tracks,
    List<EffectDto>? BusEffects,
    double? MasterGainDb,
    List<EffectDto>? MasterEffects,
    List<NestedTimelineDto>? Nested,
    List<MulticamSourceDto>? Multicams,
    List<string> Media);

/// <summary>A nested sequence reachable from the hashed range, hashed whole (id + full timeline).</summary>
internal sealed record NestedTimelineDto(Guid Id, TimelineDto Timeline);
