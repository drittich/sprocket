using Sprocket.Core.Commands;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Builds the "create a multicam source from a set of clips" edit (PLAN.md step 24, the Premiere "Create
/// Multi-Camera Source Sequence" gesture): the chosen clips become the angles of a new synced
/// <see cref="MulticamSource"/>, and a single (linked video + audio) <see cref="ClipKind.Multicam"/> clip
/// replaces them on the parent timeline. The whole thing is one undoable <see cref="CompositeCommand"/>. Pure
/// model reasoning, so it is tested headlessly even though the "which clips" comes from the (UI-bound) timeline,
/// mirroring <see cref="SequenceNesting"/>.
/// </summary>
public static class MulticamBuilder
{
    /// <summary>The built command, the new multicam source, and the parent's primary multicam clip (to select).</summary>
    public sealed record MulticamResult(CompositeCommand Command, MulticamSource Source, Clip PrimaryClip);

    /// <summary>
    /// Builds the create-multicam command for <paramref name="angleClips"/> (a selection drawn from
    /// <paramref name="parent"/>), or <see langword="null"/> when fewer than two of them form angles on a single
    /// kind of track. Each contributing clip becomes one angle (its source is the angle's video, a linked
    /// companion on an audio track supplies the angle's audio); the angles are synced by <paramref name="offsets"/>
    /// if given, else by the clips' existing placement (clips aligned on the timeline are taken as already synced).
    /// The replacement multicam clip(s) span the angles' full extent and sit on the first contributing video track
    /// and the first audio track (linked), so the picture and sound switch together when the angle changes.
    /// </summary>
    public static MulticamResult? CreateMulticam(
        Project project, Sequence parent, IReadOnlyList<Clip> angleClips, string sourceName,
        IReadOnlyList<Timecode>? offsets = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(angleClips);

        // Pair each selected clip with the parent track it lives on (drop clips not in the parent), in selection
        // order so angle index 0 is the first clip the caller listed (the default reference angle).
        var entries = new List<(Track Track, Clip Clip)>();
        foreach (Clip c in angleClips)
            if (FindTrack(parent, c) is { } track)
                entries.Add((track, c));

        // Angles come from the video clips; fall back to audio-only multicam when the selection is sound-only.
        var videoEntries = entries.Where(e => e.Track is VideoTrack).ToList();
        var audioEntries = entries.Where(e => e.Track is AudioTrack).ToList();
        bool videoMode;
        List<(Track Track, Clip Clip)> angleEntries;
        if (videoEntries.Count >= 2) { angleEntries = videoEntries; videoMode = true; }
        else if (audioEntries.Count >= 2) { angleEntries = audioEntries; videoMode = false; }
        else return null;

        if (offsets is not null && offsets.Count != angleEntries.Count)
            throw new ArgumentException("There must be one offset per angle.", nameof(offsets));

        long minTicks = long.MaxValue, maxTicks = long.MinValue;
        foreach ((_, Clip c) in angleEntries)
        {
            minTicks = Math.Min(minTicks, c.TimelineStart.Ticks);
            maxTicks = Math.Max(maxTicks, c.TimelineEnd.Ticks);
        }
        var clipStart = new Timecode(minTicks);
        var duration = new Timecode(maxTicks - minTicks);

        // Build the angles. The offset aligns each angle to multicam time 0; the default takes the clips' current
        // placement as the sync (an angle placed later on the timeline reaches its source 0 later in multicam time).
        var angles = new List<MulticamAngle>(angleEntries.Count);
        for (int i = 0; i < angleEntries.Count; i++)
        {
            Clip c = angleEntries[i].Clip;
            Timecode offset = offsets is not null
                ? offsets[i]
                : c.SourceIn + (clipStart - c.TimelineStart);
            MediaRefId? audio = videoMode ? CompanionAudioMedia(parent, c) : null;
            angles.Add(new MulticamAngle($"Cam {i + 1}", c.MediaRefId, offset, audio));
        }

        var source = new MulticamSource(MulticamId.New(), sourceName, angles);

        // Where the replacement multicam clip(s) go: the first contributing video track for the picture, the first
        // audio track carrying a companion (else the first audio track in the parent) for the sound.
        VideoTrack? videoPlace = videoMode ? (VideoTrack)angleEntries[0].Track : null;
        AudioTrack? audioPlace = videoMode
            ? FirstCompanionAudioTrack(parent, angleEntries) ?? parent.Timeline.AudioTracks.FirstOrDefault()
            : (AudioTrack)angleEntries[0].Track;

        Guid? link = videoPlace is not null && audioPlace is not null ? Guid.NewGuid() : null;
        var inserts = new List<IEditCommand>();
        Clip? primary = null;
        if (videoPlace is not null)
        {
            var v = Clip.CreateMulticamClip(source.Id, 0, duration, clipStart);
            v.LinkGroupId = link;
            inserts.Add(new AddClipCommand(videoPlace, v));
            primary = v;
        }
        if (audioPlace is not null)
        {
            var a = Clip.CreateMulticamClip(source.Id, 0, duration, clipStart);
            a.LinkGroupId = link;
            inserts.Add(new AddClipCommand(audioPlace, a));
            primary ??= a;
        }
        if (primary is null)
            return null; // nowhere to place the multicam clip

        // Remove the selected clips and their linked companions — they are now angles of the multicam source.
        var removes = new List<IEditCommand>();
        var seen = new HashSet<Clip>();
        foreach ((Track track, Clip clip) in entries)
        {
            if (seen.Add(clip))
                removes.Add(new RemoveClipCommand(track, clip));
            foreach ((Track lt, Clip lc) in parent.Timeline.ClipsLinkedTo(clip))
                if (seen.Add(lc))
                    removes.Add(new RemoveClipCommand(lt, lc));
        }

        var commands = new List<IEditCommand> { new AddMulticamSourceCommand(project, source) };
        commands.AddRange(removes);
        commands.AddRange(inserts);
        return new MulticamResult(new CompositeCommand("Create multicam source", commands), source, primary);
    }

    private static Track? FindTrack(Sequence seq, Clip clip)
    {
        foreach (Track t in seq.Timeline.Tracks)
            if (t.Clips.Contains(clip))
                return t;
        return null;
    }

    /// <summary>The media of a video clip's linked audio companion (a different source on an audio track), used as
    /// the angle's separate audio; <see langword="null"/> when there is none (audio comes from the video file).</summary>
    private static MediaRefId? CompanionAudioMedia(Sequence parent, Clip videoClip)
    {
        foreach ((Track lt, Clip lc) in parent.Timeline.ClipsLinkedTo(videoClip))
            if (lt is AudioTrack && lc.Kind == ClipKind.Media && lc.MediaRefId.Value != videoClip.MediaRefId.Value)
                return lc.MediaRefId;
        return null;
    }

    private static AudioTrack? FirstCompanionAudioTrack(Sequence parent, List<(Track Track, Clip Clip)> angleEntries)
    {
        foreach ((_, Clip clip) in angleEntries)
            foreach ((Track lt, Clip _) in parent.Timeline.ClipsLinkedTo(clip))
                if (lt is AudioTrack at)
                    return at;
        return null;
    }
}
