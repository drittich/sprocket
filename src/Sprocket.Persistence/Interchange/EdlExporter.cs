using System.Text;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Persistence.Interchange;

/// <summary>
/// Exports the active sequence as a <b>CMX3600 EDL</b> (PLAN.md step 28) — the lowest-common-denominator edit
/// decision list every NLE and online system reads. An EDL is a flat, record-ordered list of cut events with source
/// and record timecodes; it carries far less than Sprocket's model, so everything it cannot represent (effects,
/// transitions, layering, retimes, generated clips) is <b>reported</b> via an <see cref="InterchangeReport"/>, never
/// silently dropped. CMX3600 supports a single video track and up to four audio channels; the primary video track
/// and the first four audio tracks are emitted, and any others are reported as dropped.
/// </summary>
public static class EdlExporter
{
    private const int MaxAudioChannels = 4;

    /// <summary>Builds the EDL text for <paramref name="project"/>'s active sequence, plus a lossy-conversion report.</summary>
    public static InterchangeExport Export(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var report = new InterchangeReport();
        Sequence sequence = project.ActiveSequence;
        Timeline timeline = sequence.Timeline;
        Rational fps = timeline.FrameRate;

        var events = CollectEvents(project, timeline, report);
        // Record order (then channel), like a linear playout list. Renumber 001…N after sorting.
        events.Sort((a, b) =>
        {
            int byRecord = a.RecordIn.Ticks.CompareTo(b.RecordIn.Ticks);
            return byRecord != 0 ? byRecord : a.ChannelOrder.CompareTo(b.ChannelOrder);
        });

        var sb = new StringBuilder();
        sb.Append("TITLE: ").AppendLine(SanitizeTitle(sequence.Name));
        sb.Append("FCM: ").AppendLine(SmpteTimecode.FrameCodeMode(fps));

        int number = 1;
        foreach (EdlEvent e in events)
        {
            string reel = ReelName(e.ClipName);
            sb.AppendLine(
                $"{number:D3}  {reel,-8} {e.Channel,-4} C    " +
                $"{SmpteTimecode.Format(e.SourceIn, fps)} {SmpteTimecode.Format(e.SourceOut, fps)} " +
                $"{SmpteTimecode.Format(e.RecordIn, fps)} {SmpteTimecode.Format(e.RecordOut, fps)}");
            sb.Append("* FROM CLIP NAME: ").AppendLine(e.ClipName);
            number++;
        }

        if (timeline.Markers.Count > 0)
            report.Warn($"Sequence markers not exported to EDL ({timeline.Markers.Count}).");

        return new InterchangeExport(sb.ToString(), report);
    }

    /// <summary>Exports the project's active sequence to an EDL file at <paramref name="path"/>, returning the
    /// lossy-conversion report.</summary>
    public static InterchangeReport Save(Project project, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        InterchangeExport export = Export(project);
        File.WriteAllText(path, export.Text);
        return export.Report;
    }

    private static List<EdlEvent> CollectEvents(Project project, Timeline timeline, InterchangeReport report)
    {
        var events = new List<EdlEvent>();
        var videoTracks = timeline.VideoTracks.ToList();
        var audioTracks = timeline.AudioTracks.ToList();

        // CMX3600 has a single video track — export the first (bottom) one; report the rest as dropped.
        if (videoTracks.Count > 0)
            AddClipEvents(events, project, videoTracks[0], "V", channelOrder: 0, report);
        if (videoTracks.Count > 1)
            report.Warn($"Only one video track fits an EDL; {videoTracks.Count - 1} additional video track(s) dropped.");

        for (int i = 0; i < audioTracks.Count && i < MaxAudioChannels; i++)
        {
            string channel = i == 0 ? "A" : $"A{i + 1}";
            AddClipEvents(events, project, audioTracks[i], channel, channelOrder: i + 1, report);
        }
        if (audioTracks.Count > MaxAudioChannels)
            report.Warn($"EDL allows {MaxAudioChannels} audio channels; {audioTracks.Count - MaxAudioChannels} additional audio track(s) dropped.");

        return events;
    }

    private static void AddClipEvents(
        List<EdlEvent> events, Project project, Track track, string channel, int channelOrder, InterchangeReport report)
    {
        if (track.Transitions.Count > 0)
            report.Warn($"Transitions on track '{track.Name}' exported as plain cuts ({track.Transitions.Count}).");

        foreach (Clip clip in track.Clips)
        {
            if (clip.Effects.Count > 0)
                report.Count("Clip effects dropped");
            if (clip.SpeedRatio != Rational.One)
                report.Count("Clip retime/speed dropped");
            if (clip.Kind != ClipKind.Media)
                report.Count($"Non-media ({clip.Kind}) clip exported as a placeholder event");

            // Keep the event a valid cut: source duration mirrors record duration (exact for un-retimed clips).
            Timecode recordDuration = clip.TimelineEnd - clip.TimelineStart;
            events.Add(new EdlEvent(
                channel, channelOrder,
                SourceIn: clip.SourceIn,
                SourceOut: clip.SourceIn + recordDuration,
                RecordIn: clip.TimelineStart,
                RecordOut: clip.TimelineEnd,
                ClipName: ClipName(project, clip)));
        }
    }

    private static string ClipName(Project project, Clip clip)
    {
        if (clip.Kind == ClipKind.Media && project.MediaPool.Get(clip.MediaRefId) is { } media && !string.IsNullOrEmpty(media.AbsolutePath))
            return Path.GetFileName(media.AbsolutePath);
        return clip.Kind switch
        {
            ClipKind.Media => "OFFLINE",
            ClipKind.Generator => clip.Generator?.GeneratorTypeId ?? "GENERATOR",
            _ => clip.Kind.ToString().ToUpperInvariant(),
        };
    }

    /// <summary>A CMX3600 reel: up to 8 uppercase alphanumerics derived from the clip name, or "AX" when there is
    /// nothing usable (the full name is always preserved in the "* FROM CLIP NAME" comment).</summary>
    private static string ReelName(string clipName)
    {
        var sb = new StringBuilder(8);
        foreach (char c in Path.GetFileNameWithoutExtension(clipName))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                if (sb.Length == 8)
                    break;
            }
        }
        return sb.Length > 0 ? sb.ToString() : "AX";
    }

    private static string SanitizeTitle(string name)
    {
        string trimmed = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length == 0 ? "Untitled" : trimmed;
    }

    private readonly record struct EdlEvent(
        string Channel, int ChannelOrder, Timecode SourceIn, Timecode SourceOut, Timecode RecordIn, Timecode RecordOut,
        string ClipName);
}
