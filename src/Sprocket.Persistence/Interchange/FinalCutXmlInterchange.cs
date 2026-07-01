using System.Xml.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Persistence.Interchange;

/// <summary>An interchange <b>import</b> result: the reconstructed project and its lossy-conversion report.</summary>
public sealed record InterchangeImport(Project Project, InterchangeReport Report);

/// <summary>
/// Round-trips the active sequence through <b>Final Cut Pro 7 XML</b> (<c>xmeml</c>, version 5) — the lingua franca
/// that Premiere Pro, DaVinci Resolve, and Final Cut 7 all read and write (PLAN.md step 28). It carries the cut:
/// sequence name / rate / resolution, the video and audio track layout, each clip's record placement and source
/// in/out, and the source file references (id + path). Richer model state (effects, transitions, retimes, track mix,
/// generated / nested / multicam clips, and source technical metadata) has no xmeml representation and is
/// <b>reported</b> via an <see cref="InterchangeReport"/>, never silently dropped.
/// </summary>
/// <remarks>
/// Frame-based interchange snaps positions to whole frames (true of every NLE); Sprocket's clips are frame-aligned,
/// so an export→import round-trip of a cut is exact. Source in/out are expressed in the sequence timebase.
/// </remarks>
public static class FinalCutXmlInterchange
{
    // ---- export ----

    /// <summary>Builds the Final Cut XML document for <paramref name="project"/>'s active sequence, plus a report.</summary>
    public static InterchangeExport Export(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var report = new InterchangeReport();
        Sequence sequence = project.ActiveSequence;
        Timeline timeline = sequence.Timeline;
        Rational fps = timeline.FrameRate;

        var emittedFiles = new HashSet<Guid>();
        int clipItemNo = 0;

        var videoTrackEls = new List<XElement>();
        foreach (VideoTrack vt in timeline.VideoTracks)
            videoTrackEls.Add(BuildTrack(project, vt, fps, emittedFiles, ref clipItemNo, report));

        var audioTrackEls = new List<XElement>();
        foreach (AudioTrack at in timeline.AudioTracks)
            audioTrackEls.Add(BuildTrack(project, at, fps, emittedFiles, ref clipItemNo, report));

        var videoEl = new XElement("video",
            new XElement("format",
                new XElement("samplecharacteristics",
                    new XElement("width", timeline.Resolution.Width),
                    new XElement("height", timeline.Resolution.Height),
                    Rate(fps))));
        videoEl.Add(videoTrackEls);

        var audioEl = new XElement("audio",
            new XElement("format",
                new XElement("samplecharacteristics",
                    new XElement("samplerate", timeline.SampleRate))));
        audioEl.Add(audioTrackEls);

        var sequenceEl = new XElement("sequence",
            new XAttribute("id", "sequence-" + sequence.Id.Value.ToString("D")),
            new XElement("name", sequence.Name),
            new XElement("duration", Frames(timeline.Duration, fps)),
            Rate(fps),
            new XElement("media", videoEl, audioEl));

        ReportUnrepresentable(timeline, report);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("xmeml", null, null, null),
            new XElement("xmeml", new XAttribute("version", "5"), sequenceEl));

        return new InterchangeExport(doc.ToString(), report);
    }

    /// <summary>Exports the project's active sequence to a Final Cut XML file, returning the lossy report.</summary>
    public static InterchangeReport Save(Project project, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        InterchangeExport export = Export(project);
        File.WriteAllText(path, export.Text);
        return export.Report;
    }

    private static XElement BuildTrack(
        Project project, Track track, Rational fps, HashSet<Guid> emittedFiles, ref int clipItemNo, InterchangeReport report)
    {
        var trackEl = new XElement("track", new XElement("enabled", Bool(track.Enabled)));
        if (track.Transitions.Count > 0)
            report.Warn($"Transitions on track '{track.Name}' not exported ({track.Transitions.Count}).");

        foreach (Clip clip in track.Clips)
        {
            if (clip.Effects.Count > 0)
                report.Count("Clip effect not exported");
            if (clip.SpeedRatio != Rational.One)
                report.Count("Clip speed/retime not exported");
            if (clip.Kind != ClipKind.Media)
            {
                report.Count($"Non-media ({clip.Kind}) clip not exported");
                continue; // no source file to reference
            }

            clipItemNo++;
            trackEl.Add(BuildClipItem(project, clip, fps, clipItemNo, emittedFiles));
        }
        return trackEl;
    }

    private static XElement BuildClipItem(
        Project project, Clip clip, Rational fps, int clipItemNo, HashSet<Guid> emittedFiles)
    {
        MediaRef? media = project.MediaPool.Get(clip.MediaRefId);
        string name = media is not null && !string.IsNullOrEmpty(media.AbsolutePath)
            ? Path.GetFileName(media.AbsolutePath)
            : "offline";

        long inFrame = Frames(clip.SourceIn, fps);
        long outFrame = Frames(clip.SourceOut, fps);
        return new XElement("clipitem",
            new XAttribute("id", "clipitem-" + clipItemNo),
            new XElement("name", name),
            new XElement("enabled", "TRUE"),
            new XElement("duration", outFrame - inFrame),
            Rate(fps),
            new XElement("start", Frames(clip.TimelineStart, fps)),
            new XElement("end", Frames(clip.TimelineEnd, fps)),
            new XElement("in", inFrame),
            new XElement("out", outFrame),
            BuildFileRef(clip.MediaRefId, media, fps, emittedFiles));
    }

    /// <summary>The full <c>&lt;file&gt;</c> definition on first use of a source id; a lean id-only reference after
    /// (the xmeml convention that keeps repeated uses compact).</summary>
    private static XElement BuildFileRef(MediaRefId id, MediaRef? media, Rational fps, HashSet<Guid> emittedFiles)
    {
        string fileId = "file-" + id.Value.ToString("D");
        if (!emittedFiles.Add(id.Value))
            return new XElement("file", new XAttribute("id", fileId));

        var fileEl = new XElement("file", new XAttribute("id", fileId));
        if (media is null)
            return fileEl;

        fileEl.Add(new XElement("name", Path.GetFileName(media.AbsolutePath)));
        if (!string.IsNullOrEmpty(media.AbsolutePath))
            fileEl.Add(new XElement("pathurl", ToPathUrl(media.AbsolutePath)));
        fileEl.Add(Rate(fps));
        fileEl.Add(new XElement("duration", Frames(media.Info.Duration, fps)));

        var mediaEl = new XElement("media");
        if (media.Info.HasVideo)
            mediaEl.Add(new XElement("video",
                new XElement("samplecharacteristics",
                    new XElement("width", media.Info.Width),
                    new XElement("height", media.Info.Height))));
        if (media.Info.HasAudio)
            mediaEl.Add(new XElement("audio",
                new XElement("samplerate", media.Info.SampleRate),
                new XElement("channelcount", media.Info.Channels)));
        fileEl.Add(mediaEl);
        return fileEl;
    }

    private static void ReportUnrepresentable(Timeline timeline, InterchangeReport report)
    {
        if (timeline.Markers.Count > 0)
            report.Warn($"Sequence markers not exported ({timeline.Markers.Count}).");

        bool trackMix = timeline.VideoTracks.Any(v => v.Opacity != 1.0 || v.BlendMode != BlendMode.Normal)
            || timeline.AudioTracks.Any(a => a.GainDb != 0 || a.Muted || a.Solo);
        if (trackMix)
            report.Warn("Track opacity/blend and audio gain/mute/solo are not represented in Final Cut XML.");

        bool techMeta = timeline.Tracks
            .SelectMany(t => t.Clips)
            .Any(c => c.Markers.Count > 0);
        if (techMeta)
            report.Warn("Clip markers are not represented in Final Cut XML.");
    }

    // ---- import ----

    /// <summary>Parses a Final Cut XML document into a project (its single sequence), plus a report of anything in
    /// the source XML that Sprocket could not import. Throws <see cref="InvalidDataException"/> when the document is
    /// not recognisable xmeml.</summary>
    public static InterchangeImport Import(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);
        var report = new InterchangeReport();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("The Final Cut XML is not well-formed.", ex);
        }

        XElement? sequenceEl = doc.Root?.Element("sequence");
        if (doc.Root?.Name != "xmeml" || sequenceEl is null)
            throw new InvalidDataException("Not a Final Cut XML (xmeml) document with a sequence.");

        Rational fps = ParseRate(sequenceEl.Element("rate"));
        XElement? mediaEl = sequenceEl.Element("media");
        XElement? videoEl = mediaEl?.Element("video");
        XElement? audioEl = mediaEl?.Element("audio");

        (int width, int height) = ParseVideoFormat(videoEl);
        int sampleRate = ParseSampleRate(audioEl);

        var timeline = new Timeline(fps, new Resolution(width, height), sampleRate);
        var project = new Project(timeline);
        project.ActiveSequence.Name = sequenceEl.Element("name")?.Value ?? project.ActiveSequence.Name;

        // Two passes: first materialise every source file (full defs carry the detail; refs point back by id), then
        // build the tracks/clips against that pool.
        var fileIds = new Dictionary<string, MediaRefId>(StringComparer.Ordinal);
        CollectFiles(mediaEl, project, fps, fileIds, report);

        foreach (XElement trackEl in videoEl?.Elements("track") ?? [])
            timeline.Tracks.Add(BuildImportedTrack(new VideoTrack(), trackEl, project, fps, fileIds, report));
        foreach (XElement trackEl in audioEl?.Elements("track") ?? [])
            timeline.Tracks.Add(BuildImportedTrack(new AudioTrack(), trackEl, project, fps, fileIds, report));

        return new InterchangeImport(project, report);
    }

    /// <summary>Loads and imports a Final Cut XML file.</summary>
    public static InterchangeImport Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Import(File.ReadAllText(path));
    }

    private static void CollectFiles(
        XElement? mediaEl, Project project, Rational fps, Dictionary<string, MediaRefId> fileIds, InterchangeReport report)
    {
        if (mediaEl is null)
            return;
        foreach (XElement fileEl in mediaEl.Descendants("file"))
        {
            string? fileId = fileEl.Attribute("id")?.Value;
            if (fileId is null || fileIds.ContainsKey(fileId))
                continue; // a lean reference to an already-seen file, or an anonymous one — nothing new to add
            if (!fileEl.HasElements)
                continue; // reference-only occurrence before its full definition; the full one will be picked up

            MediaRefId id = ParseFileId(fileId);
            fileIds[fileId] = id;

            string path = FromPathUrl(fileEl.Element("pathurl")?.Value);
            XElement? fm = fileEl.Element("media");
            XElement? fv = fm?.Element("video");
            XElement? fa = fm?.Element("audio");
            bool hasVideo = fv is not null;
            bool hasAudio = fa is not null;
            (int w, int h) = ParseVideoFormat(fv);
            int sr = ParseInt(fa?.Element("samplerate"), 0);
            int ch = ParseInt(fa?.Element("channelcount"), hasAudio ? 2 : 0);
            long durFrames = ParseLong(fileEl.Element("duration"), 0);

            var info = new ProbedMediaInfo(
                Timecode.FromFrames(durFrames, fps), hasVideo, hasVideo ? fps : Rational.Zero, w, h,
                hasAudio, sr, ch);
            project.MediaPool.Add(new MediaRef(id, path, info));
        }
    }

    private static Track BuildImportedTrack(
        Track track, XElement trackEl, Project project, Rational fps, Dictionary<string, MediaRefId> fileIds,
        InterchangeReport report)
    {
        track.Enabled = ParseBool(trackEl.Element("enabled"), defaultValue: true);
        foreach (XElement item in trackEl.Elements())
        {
            switch (item.Name.LocalName)
            {
                case "clipitem":
                    if (TryBuildClip(item, fps, fileIds, report) is { } clip)
                        track.Clips.Add(clip);
                    break;
                case "transitionitem":
                    report.Warn("A transition in the source XML was not imported.");
                    break;
                case "generatoritem":
                    report.Warn("A generator item in the source XML was not imported.");
                    break;
            }
        }
        return track;
    }

    private static Clip? TryBuildClip(
        XElement item, Rational fps, Dictionary<string, MediaRefId> fileIds, InterchangeReport report)
    {
        if (item.Element("filters") is not null || item.Elements("filter").Any())
            report.Warn("Filters on an imported clip were not applied.");

        string? fileId = item.Element("file")?.Attribute("id")?.Value;
        if (fileId is null || !fileIds.TryGetValue(fileId, out MediaRefId mediaId))
        {
            report.Warn("A clip referencing an unknown/offline file was skipped.");
            return null;
        }

        long start = ParseLong(item.Element("start"), 0);
        long inFrame = ParseLong(item.Element("in"), 0);
        long outFrame = ParseLong(item.Element("out"), 0);
        if (outFrame < inFrame)
            (inFrame, outFrame) = (outFrame, inFrame);

        return new Clip(
            mediaId,
            Timecode.FromFrames(inFrame, fps),
            Timecode.FromFrames(outFrame, fps),
            Timecode.FromFrames(start, fps));
    }

    // ---- shared helpers ----

    private static XElement Rate(Rational fps) => new(
        "rate",
        new XElement("timebase", SmpteTimecode.NominalRate(fps)),
        new XElement("ntsc", Bool(SmpteTimecode.IsNtsc(fps))));

    private static Rational ParseRate(XElement? rateEl)
    {
        int timebase = ParseInt(rateEl?.Element("timebase"), 30);
        bool ntsc = ParseBool(rateEl?.Element("ntsc"), defaultValue: false);
        if (timebase <= 0)
            timebase = 30;
        // NTSC rates are timebase×1000/1001 (29.97, 23.976, 59.94); integer rates are timebase/1.
        return ntsc ? new Rational(timebase * 1000, 1001) : new Rational(timebase, 1);
    }

    private static (int Width, int Height) ParseVideoFormat(XElement? videoEl)
    {
        XElement? sc = videoEl?.Element("format")?.Element("samplecharacteristics")
            ?? videoEl?.Element("samplecharacteristics");
        return (ParseInt(sc?.Element("width"), 1920), ParseInt(sc?.Element("height"), 1080));
    }

    private static int ParseSampleRate(XElement? audioEl)
    {
        XElement? sc = audioEl?.Element("format")?.Element("samplecharacteristics");
        return ParseInt(sc?.Element("samplerate"), 48000);
    }

    private static long Frames(Timecode time, Rational fps) => Math.Max(0, time.ToFrameIndex(fps));

    private static string Bool(bool value) => value ? "TRUE" : "FALSE";

    private static bool ParseBool(XElement? el, bool defaultValue) =>
        el is null ? defaultValue : string.Equals(el.Value.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(XElement? el, int defaultValue) =>
        el is not null && int.TryParse(el.Value, out int v) ? v : defaultValue;

    private static long ParseLong(XElement? el, long defaultValue) =>
        el is not null && long.TryParse(el.Value, out long v) ? v : defaultValue;

    /// <summary>A file id of the form <c>file-{guid}</c> (Sprocket's own export) preserves the source id; any other
    /// id string is mapped to a fresh, stable id per document.</summary>
    private static MediaRefId ParseFileId(string fileId)
    {
        const string prefix = "file-";
        if (fileId.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(fileId.AsSpan(prefix.Length), out Guid g))
            return new MediaRefId(g);
        return MediaRefId.New();
    }

    private static string ToPathUrl(string absolutePath)
    {
        try
        {
            return new Uri(absolutePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return absolutePath;
        }
    }

    private static string FromPathUrl(string? pathUrl)
    {
        if (string.IsNullOrEmpty(pathUrl))
            return string.Empty;
        if (Uri.TryCreate(pathUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            return uri.LocalPath;
        return pathUrl;
    }
}
