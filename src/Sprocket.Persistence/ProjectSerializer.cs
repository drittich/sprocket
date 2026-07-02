using System.Text.Json;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Persistence;

/// <summary>
/// Saves and loads a <see cref="Project"/> as versioned JSON (ARCHITECTURE.md §12, PLAN.md steps 9 &amp; 28). The
/// model is plain data, so it round-trips losslessly through the DTO layer; the file carries a
/// <see cref="SchemaVersion"/> for forward migration. Loading is <b>offline-tolerant</b>: a media file that
/// can't be found is kept in the pool with its stored path (rendering as black/silence) rather than failing.
/// </summary>
/// <remarks>
/// <para><b>Collaboration-ready format split (PLAN.md step 28).</b> The shared, committed project file references
/// each source by stable <see cref="MediaRefId"/> only; the per-user asset paths live in a separate
/// <see cref="MediaLinks">media-link sidecar</see> so a pulled project-file change never forces a relink. Hence:</para>
/// <list type="bullet">
///   <item><see cref="Save"/> / <see cref="Load"/> operate on the <b>pair</b>: the project file (id + info, no
///     paths) plus its <c>.links.json</c> sidecar (id → local path). This is the diffable, merge-friendly form.</item>
///   <item><see cref="Serialize"/> (to a single string) is <b>self-contained</b> — it inlines the paths, since a
///     lone string has nowhere else to put them. Used for autosave/recovery and in-memory snapshots.</item>
/// </list>
/// <para>On load, a sidecar link wins; failing that an inlined path (a self-contained/pre-step-28 file) is used;
/// failing both the source is offline and awaits relink (PLAN.md step 28). No schema bump was needed — the path
/// fields simply became additive/nullable, matching the format's established backward-compatibility discipline.</para>
/// </remarks>
public static class ProjectSerializer
{
    /// <summary>The current on-disk schema version. Bumped when the DTO shape changes incompatibly.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Serializes <paramref name="project"/> to a <b>self-contained</b> JSON string with media paths inlined (the
    /// autosave / snapshot form — a lone string has nowhere else to put them). When <paramref name="projectFilePath"/>
    /// is given, each inlined path also gets a project-relative variant so a moved snapshot relinks. The
    /// collaboration-ready committed form (paths in a sidecar) is written by <see cref="Save"/>.
    /// </summary>
    public static string Serialize(Project project, string? projectFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? projectDir = projectFilePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        ProjectDto dto = ToDto(project, projectDir, inlineMediaPaths: true);
        return JsonSerializer.Serialize(dto, SprocketJsonContext.Default.ProjectDto);
    }

    /// <summary>
    /// Writes <paramref name="project"/> to <paramref name="path"/> in the collaboration-ready split form: the
    /// project file references sources by id (no inlined paths), and a <c>.links.json</c> sidecar beside it records
    /// this machine's local paths (PLAN.md step 28). Loading the pair relinks; sharing only the project file loads
    /// the sources offline until relinked.
    /// </summary>
    public static void Save(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? projectDir = Path.GetDirectoryName(Path.GetFullPath(path));
        ProjectDto dto = ToDto(project, projectDir, inlineMediaPaths: false);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, SprocketJsonContext.Default.ProjectDto));
        MediaLinks.Write(project, path);
    }

    /// <summary>
    /// Parses a project from a JSON string. <paramref name="projectDirectory"/>, when given, resolves relative
    /// media paths. <paramref name="links"/>, when given, is the resolved id→path map from a media-link sidecar and
    /// takes precedence over any path inlined in the JSON (PLAN.md step 28). Throws <see cref="InvalidDataException"/>
    /// on malformed JSON or an unknown schema.
    /// </summary>
    public static Project Deserialize(
        string json, string? projectDirectory = null, IReadOnlyDictionary<MediaRefId, string>? links = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, SprocketJsonContext.Default.ProjectDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The project file is not valid JSON.", ex);
        }
        if (dto is null)
            throw new InvalidDataException("The project file is empty.");
        if (dto.SchemaVersion != SchemaVersion)
            throw new InvalidDataException(
                $"Unsupported project schema version {dto.SchemaVersion} (this build reads version {SchemaVersion}).");

        return FromDto(dto, projectDirectory, links);
    }

    /// <summary>
    /// Loads a project from the JSON file at <paramref name="path"/> together with its media-link sidecar (PLAN.md
    /// step 28): the sidecar supplies each source's local path; sources with no link (a fresh clone, or a source
    /// whose file is gone) load offline and await relink. Relative paths are resolved against the file's directory.
    /// </summary>
    public static Project Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        IReadOnlyDictionary<MediaRefId, string> links = MediaLinks.Read(fullPath);
        return Deserialize(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath), links);
    }

    // ---- model → DTO ----

    private static ProjectDto ToDto(Project project, string? projectDir, bool inlineMediaPaths)
    {
        var media = new List<MediaRefDto>();
        foreach (MediaRef m in project.MediaPool.Items)
            media.Add(ToDto(m, projectDir, inlineMediaPaths));

        var settings = new SettingsDto(
            project.Settings.MasterGainDb, project.Settings.UseProxies, project.Settings.ProxyTier,
            ToEffectList(project.Settings.MasterAudioEffects));

        // Multicam sources are orthogonal to the sequence shape; null (omitted) when there are none, so a
        // multicam-free project serializes byte-identically (WhenWritingNull).
        List<MulticamSourceDto>? multicams = ToMulticamList(project.MulticamSources);

        // Single sequence with no nesting → the legacy Timeline-only shape, byte-identical to pre-step-23 files.
        // Multiple sequences or any nested-sequence clip → the Sequences shape (all sequences + the active id).
        if (project.Sequences.Count <= 1 && !HasAnySequenceClip(project))
            return new ProjectDto(SchemaVersion, media, ToDto(project.Timeline), settings, MulticamSources: multicams);

        var sequences = new List<SequenceDto>(project.Sequences.Count);
        foreach (Sequence s in project.Sequences)
            sequences.Add(new SequenceDto(s.Id.Value, s.Name, ToDto(s.Timeline)));
        return new ProjectDto(
            SchemaVersion, media, Timeline: null, settings, sequences, project.ActiveSequence.Id.Value, multicams);
    }

    /// <summary>Converts the multicam sources to DTOs, returning <see langword="null"/> when there are none so a
    /// multicam-free project serializes byte-identically to a pre-step-24 file (WhenWritingNull).</summary>
    internal static List<MulticamSourceDto>? ToMulticamList(List<MulticamSource> sources)
    {
        if (sources.Count == 0)
            return null;
        var list = new List<MulticamSourceDto>(sources.Count);
        foreach (MulticamSource s in sources)
        {
            var angles = new List<MulticamAngleDto>(s.Angles.Count);
            foreach (MulticamAngle a in s.Angles)
                angles.Add(new MulticamAngleDto(a.Name, a.MediaRefId.Value, a.SyncOffset.Ticks, a.AudioMediaRefId?.Value));
            list.Add(new MulticamSourceDto(s.Id.Value, s.Name, angles));
        }
        return list;
    }

    /// <summary>Whether any sequence holds a nested-sequence clip — which forces the multi-sequence wire shape.</summary>
    private static bool HasAnySequenceClip(Project project)
    {
        foreach (Sequence s in project.Sequences)
            foreach (Track t in s.Timeline.Tracks)
                foreach (Clip c in t.Clips)
                    if (c.Kind == ClipKind.Sequence)
                        return true;
        return false;
    }

    /// <summary>Converts a media ref to its DTO. In the collaboration-ready split form
    /// (<paramref name="inlineMediaPaths"/> = <see langword="false"/>, used by <see cref="Save"/>) the path fields
    /// are omitted — the source is referenced by id only and its path lives in the media-link sidecar. In the
    /// self-contained form (used by <see cref="Serialize"/>) the absolute path (and a project-relative variant) are
    /// inlined so the single string round-trips on its own (PLAN.md step 28).</summary>
    private static MediaRefDto ToDto(MediaRef media, string? projectDir, bool inlineMediaPaths)
    {
        ProbedInfoDto info = ToDto(media.Info);
        if (!inlineMediaPaths || string.IsNullOrEmpty(media.AbsolutePath))
            return new MediaRefDto(media.Id.Value, info);

        string? relative = null;
        if (projectDir is not null)
        {
            string rel = Path.GetRelativePath(projectDir, media.AbsolutePath);
            // GetRelativePath returns the input unchanged when there's no shared root (e.g. another drive);
            // only store it when it actually expresses a relative location.
            if (rel != media.AbsolutePath && !Path.IsPathRooted(rel))
                relative = rel;
        }
        return new MediaRefDto(media.Id.Value, info, media.AbsolutePath, relative);
    }

    private static ProbedInfoDto ToDto(ProbedMediaInfo i) => new(
        i.Duration.Ticks, i.HasVideo, ToDto(i.FrameRate), i.Width, i.Height, i.HasAudio, i.SampleRate, i.Channels,
        i.HasAlpha ? true : null,
        // Write only non-default informational fields (WhenWritingNull) so opaque/8-bit/SDR/CFR media keeps a
        // minimal, stable diff and pre-27 files round-trip byte-identically.
        VideoCodec: string.IsNullOrEmpty(i.VideoCodec) ? null : i.VideoCodec,
        AudioCodec: string.IsNullOrEmpty(i.AudioCodec) ? null : i.AudioCodec,
        PixelFormatName: string.IsNullOrEmpty(i.PixelFormatName) ? null : i.PixelFormatName,
        BitDepth: i.BitDepth == 8 ? null : i.BitDepth,
        IsHdr: i.IsHdr ? true : null,
        IsVariableFrameRate: i.IsVariableFrameRate ? true : null);

    internal static TimelineDto ToDto(Timeline t)
    {
        var tracks = new List<TrackDto>();
        foreach (Track track in t.Tracks)
            tracks.Add(ToDto(track));
        return new TimelineDto(ToDto(t.FrameRate), new ResolutionDto(t.Resolution.Width, t.Resolution.Height),
            t.SampleRate, tracks, ToMarkerList(t.Markers), ToEffectList(t.AudioEffects));
    }

    /// <summary>Converts an audio effect chain to DTOs, returning <see langword="null"/> when empty so a
    /// chain-less track/timeline/project serializes byte-identically to a pre-step-31 file (WhenWritingNull).</summary>
    internal static List<EffectDto>? ToEffectList(List<EffectInstance> effects)
    {
        if (effects.Count == 0)
            return null;
        var list = new List<EffectDto>(effects.Count);
        foreach (EffectInstance e in effects)
            list.Add(ToDto(e));
        return list;
    }

    /// <summary>Converts a marker list to DTOs, returning <see langword="null"/> when empty so a marker-less
    /// timeline/clip serializes byte-identically to a pre-step-20 file (WhenWritingNull).</summary>
    private static List<MarkerDto>? ToMarkerList(List<Marker> markers)
    {
        if (markers.Count == 0)
            return null;
        var list = new List<MarkerDto>(markers.Count);
        foreach (Marker m in markers)
            list.Add(new MarkerDto(m.Time.Ticks, m.Name, m.Comment, m.Color, m.Duration.Ticks));
        return list;
    }

    internal static TrackDto ToDto(Track track)
    {
        var clips = new List<ClipDto>();
        foreach (Clip c in track.Clips)
            clips.Add(ToDto(c));

        List<TransitionDto>? transitions = ToTransitionList(track.Transitions);

        return track switch
        {
            VideoTrack v => new TrackDto(TrackKind.Video, v.Name, v.Enabled, v.Opacity, v.BlendMode, 0, false, false, clips, transitions),
            AudioTrack a => new TrackDto(TrackKind.Audio, a.Name, a.Enabled, 1.0, BlendMode.Normal, a.GainDb, a.Muted, a.Solo, clips, transitions, a.Pan != 0 ? a.Pan : null, ToEffectList(a.Effects)),
            _ => throw new NotSupportedException($"Unknown track type {track.GetType().Name}."),
        };
    }

    /// <summary>Converts a track's transitions to DTOs, returning <see langword="null"/> when there are none so a
    /// transition-free track serializes byte-identically to a pre-step-25 file (WhenWritingNull).</summary>
    private static List<TransitionDto>? ToTransitionList(List<Transition> transitions)
    {
        if (transitions.Count == 0)
            return null;
        var list = new List<TransitionDto>(transitions.Count);
        foreach (Transition t in transitions)
            list.Add(new TransitionDto(
                t.TransitionTypeId, t.CutPoint.Ticks, t.Duration.Ticks, t.Alignment, ToParameterDict(t.Parameters)));
        return list;
    }

    /// <summary>Converts an animatable-parameter bag to DTOs, returning <see langword="null"/> when empty so a
    /// parameter-less transition (the v1 built-ins) serializes compactly (WhenWritingNull).</summary>
    private static Dictionary<string, AnimatableValueDto>? ToParameterDict(Dictionary<string, AnimatableValue> parameters)
    {
        if (parameters.Count == 0)
            return null;
        var dict = new Dictionary<string, AnimatableValueDto>(parameters.Count);
        foreach ((string name, AnimatableValue value) in parameters)
            dict[name] = ToDto(value);
        return dict;
    }

    private static ClipDto ToDto(Clip c)
    {
        var effects = new List<EffectDto>();
        foreach (EffectInstance e in c.Effects)
            effects.Add(ToDto(e));
        // Media clips leave Kind/Generator null so the wire format is byte-identical to pre-step-19 files.
        ClipKind? kind = c.Kind == ClipKind.Media ? null : c.Kind;
        GeneratorDto? generator = c.Generator is null ? null : ToDto(c.Generator);
        // A normal-speed clip writes no speed (WhenWritingNull) so the wire format is byte-identical to pre-21 files.
        bool retimed = c.SpeedRatio != Rational.One;
        // Multicam fields only on a multicam clip, so non-multicam clips serialize byte-identically (WhenWritingNull).
        bool multicam = c.Kind == ClipKind.Multicam;
        return new ClipDto(
            c.MediaRefId.Value, c.SourceIn.Ticks, c.SourceOut.Ticks, c.TimelineStart.Ticks, effects,
            c.LinkGroupId, kind, generator, ToMarkerList(c.Markers),
            retimed ? c.SpeedRatio.Num : null, retimed ? c.SpeedRatio.Den : null,
            c.SourceSequenceId?.Value,
            multicam ? c.SourceMulticamId?.Value : null, multicam ? c.ActiveAngle : null,
            c.GainDb != 0 ? c.GainDb : null);
    }

    private static GeneratorDto ToDto(GeneratorSpec g)
    {
        var parameters = new Dictionary<string, AnimatableValueDto>(g.Parameters.Count);
        foreach ((string name, AnimatableValue value) in g.Parameters)
            parameters[name] = ToDto(value);
        return new GeneratorDto(g.GeneratorTypeId, new Dictionary<string, string>(g.Strings), parameters);
    }

    private static EffectDto ToDto(EffectInstance e)
    {
        var parameters = new Dictionary<string, AnimatableValueDto>(e.Parameters.Count);
        foreach ((string name, AnimatableValue value) in e.Parameters)
            parameters[name] = ToDto(value);
        return new EffectDto(e.EffectTypeId, parameters, e.Enabled ? null : false);
    }

    private static AnimatableValueDto ToDto(AnimatableValue value)
    {
        if (!value.IsAnimated)
            // A constant value ignores the time argument, so evaluating at zero yields the constant.
            return new AnimatableValueDto(value.Evaluate(Timecode.Zero), null);

        var keyframes = new List<KeyframeDto>(value.Keyframes.Count);
        foreach (Keyframe k in value.Keyframes)
            keyframes.Add(new KeyframeDto(k.Time.Ticks, k.Value, k.Interpolation, ToDto(k.EaseOut), ToDto(k.EaseIn)));
        return new AnimatableValueDto(null, keyframes);
    }

    private static BezierHandleDto? ToDto(BezierHandle? h) => h is { } v ? new BezierHandleDto(v.X, v.Y) : null;

    private static RationalDto ToDto(Rational r) => new(r.Num, r.Den);

    // ---- DTO → model ----

    private static Project FromDto(ProjectDto dto, string? projectDir, IReadOnlyDictionary<MediaRefId, string>? links)
    {
        Project project = BuildSequences(dto);
        foreach (MediaRefDto m in dto.Media)
            project.MediaPool.Add(FromDto(m, projectDir, links));
        project.Settings.MasterGainDb = dto.Settings.MasterGainDb;
        project.Settings.UseProxies = dto.Settings.UseProxies;
        project.Settings.ProxyTier = dto.Settings.ProxyTier;
        AddEffects(project.Settings.MasterAudioEffects, dto.Settings.MasterAudioEffects);
        if (dto.MulticamSources is { } multicams)
            foreach (MulticamSourceDto m in multicams)
                project.MulticamSources.Add(FromDto(m));
        return project;
    }

    /// <summary>Builds the project's sequences from whichever shape the file uses: the multi-sequence
    /// <see cref="ProjectDto.Sequences"/> list (preserving each sequence's id so nested-clip references resolve),
    /// or the legacy single <see cref="ProjectDto.Timeline"/> (pre-step-23 files / single-sequence projects).</summary>
    private static Project BuildSequences(ProjectDto dto)
    {
        if (dto.Sequences is { Count: > 0 } seqDtos)
        {
            var project = new Project();
            project.Sequences.Clear(); // drop the default sequence; restore the persisted ones with their ids
            Sequence? active = null;
            foreach (SequenceDto sd in seqDtos)
            {
                var seq = new Sequence(new SequenceId(sd.Id), sd.Name, FromDto(sd.Timeline));
                project.Sequences.Add(seq);
                if (sd.Id == dto.ActiveSequenceId)
                    active = seq;
            }
            project.ActiveSequence = active ?? project.Sequences[0];
            return project;
        }

        if (dto.Timeline is { } tl)
            return new Project(FromDto(tl)); // single sequence: fresh id + default name

        throw new InvalidDataException("The project file has neither a timeline nor any sequences.");
    }

    private static MediaRef FromDto(MediaRefDto m, string? projectDir, IReadOnlyDictionary<MediaRefId, string>? links)
    {
        var id = new MediaRefId(m.Id);
        string path = ResolvePath(id, m, projectDir, links);
        return new MediaRef(id, path, FromDto(m.Info));
    }

    /// <summary>
    /// Resolves a source's local path (PLAN.md step 28). A media-link sidecar entry wins (the per-user truth); then
    /// an inlined project-relative path resolved against the project directory when that file exists; then an inlined
    /// absolute path (a self-contained / pre-step-28 file). With none of those the source is offline — an empty path
    /// that renders as black/silence and surfaces in the relink workflow (§15).
    /// </summary>
    private static string ResolvePath(
        MediaRefId id, MediaRefDto m, string? projectDir, IReadOnlyDictionary<MediaRefId, string>? links)
    {
        if (links is not null && links.TryGetValue(id, out string? linked))
            return linked;
        if (projectDir is not null && m.RelativePath is not null)
        {
            string candidate = Path.GetFullPath(Path.Combine(projectDir, m.RelativePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return m.AbsolutePath ?? string.Empty;
    }

    private static ProbedMediaInfo FromDto(ProbedInfoDto i) => new(
        new Timecode(i.DurationTicks), i.HasVideo, FromDto(i.FrameRate), i.Width, i.Height, i.HasAudio, i.SampleRate, i.Channels,
        i.HasAlpha ?? false,
        VideoCodec: i.VideoCodec ?? "",
        AudioCodec: i.AudioCodec ?? "",
        PixelFormatName: i.PixelFormatName ?? "",
        BitDepth: i.BitDepth ?? 8,
        IsHdr: i.IsHdr ?? false,
        IsVariableFrameRate: i.IsVariableFrameRate ?? false);

    private static Timeline FromDto(TimelineDto t)
    {
        var timeline = new Timeline(FromDto(t.FrameRate), new Resolution(t.Resolution.Width, t.Resolution.Height), t.SampleRate);
        foreach (TrackDto track in t.Tracks)
            timeline.Tracks.Add(FromDto(track));
        AddMarkers(timeline.Markers, t.Markers);
        AddEffects(timeline.AudioEffects, t.AudioEffects);
        return timeline;
    }

    /// <summary>Restores an audio effect chain (if any) into a model chain list. Null/absent (pre-step-31
    /// files) leaves it empty.</summary>
    private static void AddEffects(List<EffectInstance> target, List<EffectDto>? dtos)
    {
        if (dtos is null)
            return;
        foreach (EffectDto e in dtos)
            target.Add(FromDto(e));
    }

    /// <summary>Restores markers (if any) into a model marker list. Null/absent (pre-step-20 files) leaves it empty.</summary>
    private static void AddMarkers(List<Marker> target, List<MarkerDto>? dtos)
    {
        if (dtos is null)
            return;
        foreach (MarkerDto m in dtos)
            target.Add(new Marker(new Timecode(m.TimeTicks), m.Name, m.Comment, m.Color, new Timecode(m.DurationTicks)));
    }

    private static Track FromDto(TrackDto t)
    {
        Track track = t.Kind switch
        {
            TrackKind.Video => new VideoTrack { Name = t.Name, Enabled = t.Enabled, Opacity = t.Opacity, BlendMode = t.BlendMode },
            TrackKind.Audio => new AudioTrack { Name = t.Name, Enabled = t.Enabled, GainDb = t.GainDb, Muted = t.Muted, Solo = t.Solo, Pan = t.Pan ?? 0 },
            _ => throw new InvalidDataException($"Unknown track kind {t.Kind}."),
        };
        if (track is AudioTrack audio)
            AddEffects(audio.Effects, t.Effects);
        foreach (ClipDto c in t.Clips)
            track.Clips.Add(FromDto(c));
        if (t.Transitions is { } transitions)
            foreach (TransitionDto td in transitions)
                track.Transitions.Add(FromDto(td));
        return track;
    }

    private static Transition FromDto(TransitionDto t)
    {
        var transition = new Transition(
            t.TransitionTypeId, new Timecode(t.CutPointTicks), new Timecode(t.DurationTicks), t.Alignment);
        if (t.Parameters is { } parameters)
            foreach ((string name, AnimatableValueDto value) in parameters)
                transition.Set(name, FromDto(value));
        return transition;
    }

    private static Clip FromDto(ClipDto c)
    {
        Timecode sourceIn = new(c.SourceInTicks);
        Timecode sourceOut = new(c.SourceOutTicks);
        Timecode start = new(c.TimelineStartTicks);

        // Kind absent ⇒ Media (pre-step-19 files / plain media clips).
        Clip clip = (c.Kind ?? ClipKind.Media) switch
        {
            ClipKind.Generator => RestoreSpan(
                Clip.CreateGenerator(FromDto(c.Generator!), sourceOut, start), sourceIn, sourceOut),
            ClipKind.Adjustment => RestoreSpan(
                Clip.CreateAdjustment(sourceOut, start), sourceIn, sourceOut),
            ClipKind.Sequence => RestoreSpan(
                Clip.CreateSequenceClip(new SequenceId(c.SourceSequenceId ?? Guid.Empty), sourceOut, start), sourceIn, sourceOut),
            ClipKind.Multicam => RestoreSpan(
                Clip.CreateMulticamClip(new MulticamId(c.SourceMulticamId ?? Guid.Empty), c.ActiveAngle ?? 0, sourceOut, start),
                sourceIn, sourceOut),
            _ => new Clip(new MediaRefId(c.MediaRefId), sourceIn, sourceOut, start),
        };
        clip.LinkGroupId = c.LinkGroupId;
        // Speed absent ⇒ 1/1 (pre-21 files / normal-speed clips).
        if (c.SpeedNum is int speedNum && c.SpeedDen is int speedDen)
            clip.SpeedRatio = new Rational(speedNum, speedDen);
        clip.GainDb = c.GainDb ?? 0; // GainDb absent ⇒ 0 dB (pre-30 files / un-gained clips)
        foreach (EffectDto e in c.Effects)
            clip.Effects.Add(FromDto(e));
        AddMarkers(clip.Markers, c.Markers);
        return clip;
    }

    /// <summary>Restores a synthetic (generator/adjustment) clip's exact trim — its factory starts the source at
    /// zero, but a trimmed/slipped clip may have a non-zero source in-point.</summary>
    private static Clip RestoreSpan(Clip clip, Timecode sourceIn, Timecode sourceOut)
    {
        clip.SourceIn = sourceIn;
        clip.SourceOut = sourceOut;
        return clip;
    }

    private static MulticamSource FromDto(MulticamSourceDto m)
    {
        var angles = new List<MulticamAngle>(m.Angles.Count);
        foreach (MulticamAngleDto a in m.Angles)
            angles.Add(new MulticamAngle(
                a.Name, new MediaRefId(a.MediaRefId), new Timecode(a.SyncOffsetTicks),
                a.AudioMediaRefId is { } aid ? new MediaRefId(aid) : null));
        return new MulticamSource(new MulticamId(m.Id), m.Name, angles);
    }

    private static GeneratorSpec FromDto(GeneratorDto g)
    {
        var spec = new GeneratorSpec(g.GeneratorTypeId);
        foreach ((string name, string value) in g.Strings)
            spec.SetString(name, value);
        foreach ((string name, AnimatableValueDto value) in g.Parameters)
            spec.Set(name, FromDto(value));
        return spec;
    }

    private static EffectInstance FromDto(EffectDto e)
    {
        var effect = new EffectInstance(e.EffectTypeId) { Enabled = e.Enabled ?? true };
        foreach ((string name, AnimatableValueDto value) in e.Parameters)
            effect.Set(name, FromDto(value));
        return effect;
    }

    private static AnimatableValue FromDto(AnimatableValueDto v)
    {
        if (v.Keyframes is { Count: > 0 } keyframes)
            return AnimatableValue.Animated(keyframes.Select(k => new Keyframe(
                new Timecode(k.TimeTicks), k.Value, k.Interpolation, FromDto(k.EaseOut), FromDto(k.EaseIn))));
        return AnimatableValue.Constant(v.Constant ?? 0);
    }

    private static BezierHandle? FromDto(BezierHandleDto? h) => h is { } v ? new BezierHandle(v.X, v.Y) : null;

    private static Rational FromDto(RationalDto r) => new(r.Num, r.Den);
}
