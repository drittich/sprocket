using System.Text.Json;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Persistence;

/// <summary>
/// Saves and loads a <see cref="Project"/> as versioned JSON (ARCHITECTURE.md §12, PLAN.md step 9). The model
/// is plain data, so it round-trips losslessly through the DTO layer; the file carries a
/// <see cref="SchemaVersion"/> for forward migration. Loading is <b>offline-tolerant</b>: a media file that
/// can't be found is kept in the pool with its stored path (it simply renders as black/silence) rather than
/// failing the load.
/// </summary>
public static class ProjectSerializer
{
    /// <summary>The current on-disk schema version. Bumped when the DTO shape changes incompatibly.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Serializes <paramref name="project"/> to a JSON string. When <paramref name="projectFilePath"/>
    /// is given, media paths are also stored relative to that file's directory so a moved project relinks.</summary>
    public static string Serialize(Project project, string? projectFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? projectDir = projectFilePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        ProjectDto dto = ToDto(project, projectDir);
        return JsonSerializer.Serialize(dto, SprocketJsonContext.Default.ProjectDto);
    }

    /// <summary>Writes <paramref name="project"/> to <paramref name="path"/> as JSON (relative media paths
    /// resolved against the file's directory).</summary>
    public static void Save(Project project, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, Serialize(project, path));
    }

    /// <summary>Parses a project from a JSON string. <paramref name="projectDirectory"/>, when given, resolves
    /// relative media paths. Throws <see cref="InvalidDataException"/> on malformed JSON or an unknown schema.</summary>
    public static Project Deserialize(string json, string? projectDirectory = null)
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

        return FromDto(dto, projectDirectory);
    }

    /// <summary>Loads a project from the JSON file at <paramref name="path"/>, resolving relative media paths
    /// against the file's directory.</summary>
    public static Project Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        return Deserialize(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath));
    }

    // ---- model → DTO ----

    private static ProjectDto ToDto(Project project, string? projectDir)
    {
        var media = new List<MediaRefDto>();
        foreach (MediaRef m in project.MediaPool.Items)
            media.Add(ToDto(m, projectDir));

        return new ProjectDto(SchemaVersion, media, ToDto(project.Timeline), new SettingsDto(project.Settings.MasterGainDb));
    }

    private static MediaRefDto ToDto(MediaRef media, string? projectDir)
    {
        string? relative = null;
        if (projectDir is not null)
        {
            string rel = Path.GetRelativePath(projectDir, media.AbsolutePath);
            // GetRelativePath returns the input unchanged when there's no shared root (e.g. another drive);
            // only store it when it actually expresses a relative location.
            if (rel != media.AbsolutePath && !Path.IsPathRooted(rel))
                relative = rel;
        }
        return new MediaRefDto(media.Id.Value, media.AbsolutePath, relative, ToDto(media.Info));
    }

    private static ProbedInfoDto ToDto(ProbedMediaInfo i) => new(
        i.Duration.Ticks, i.HasVideo, ToDto(i.FrameRate), i.Width, i.Height, i.HasAudio, i.SampleRate, i.Channels);

    private static TimelineDto ToDto(Timeline t)
    {
        var tracks = new List<TrackDto>();
        foreach (Track track in t.Tracks)
            tracks.Add(ToDto(track));
        return new TimelineDto(ToDto(t.FrameRate), new ResolutionDto(t.Resolution.Width, t.Resolution.Height), t.SampleRate, tracks);
    }

    private static TrackDto ToDto(Track track)
    {
        var clips = new List<ClipDto>();
        foreach (Clip c in track.Clips)
            clips.Add(ToDto(c));

        return track switch
        {
            VideoTrack v => new TrackDto(TrackKind.Video, v.Name, v.Enabled, v.Opacity, v.BlendMode, 0, false, false, clips),
            AudioTrack a => new TrackDto(TrackKind.Audio, a.Name, a.Enabled, 1.0, BlendMode.Normal, a.GainDb, a.Muted, a.Solo, clips),
            _ => throw new NotSupportedException($"Unknown track type {track.GetType().Name}."),
        };
    }

    private static ClipDto ToDto(Clip c)
    {
        var effects = new List<EffectDto>();
        foreach (EffectInstance e in c.Effects)
            effects.Add(ToDto(e));
        return new ClipDto(
            c.MediaRefId.Value, c.SourceIn.Ticks, c.SourceOut.Ticks, c.TimelineStart.Ticks, effects, c.LinkGroupId);
    }

    private static EffectDto ToDto(EffectInstance e)
    {
        var parameters = new Dictionary<string, AnimatableValueDto>(e.Parameters.Count);
        foreach ((string name, AnimatableValue value) in e.Parameters)
            parameters[name] = ToDto(value);
        return new EffectDto(e.EffectTypeId, parameters);
    }

    private static AnimatableValueDto ToDto(AnimatableValue value)
    {
        if (!value.IsAnimated)
            // A constant value ignores the time argument, so evaluating at zero yields the constant.
            return new AnimatableValueDto(value.Evaluate(Timecode.Zero), null);

        var keyframes = new List<KeyframeDto>(value.Keyframes.Count);
        foreach (Keyframe k in value.Keyframes)
            keyframes.Add(new KeyframeDto(k.Time.Ticks, k.Value, k.Interpolation));
        return new AnimatableValueDto(null, keyframes);
    }

    private static RationalDto ToDto(Rational r) => new(r.Num, r.Den);

    // ---- DTO → model ----

    private static Project FromDto(ProjectDto dto, string? projectDir)
    {
        var project = new Project(FromDto(dto.Timeline));
        foreach (MediaRefDto m in dto.Media)
            project.MediaPool.Add(FromDto(m, projectDir));
        project.Settings.MasterGainDb = dto.Settings.MasterGainDb;
        return project;
    }

    private static MediaRef FromDto(MediaRefDto m, string? projectDir)
    {
        string path = ResolvePath(m, projectDir);
        return new MediaRef(new MediaRefId(m.Id), path, FromDto(m.Info));
    }

    /// <summary>Prefer the relative path resolved against the project directory when that file exists; otherwise
    /// fall back to the stored absolute path (which may be offline — tolerated, §12).</summary>
    private static string ResolvePath(MediaRefDto m, string? projectDir)
    {
        if (projectDir is not null && m.RelativePath is not null)
        {
            string candidate = Path.GetFullPath(Path.Combine(projectDir, m.RelativePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return m.AbsolutePath;
    }

    private static ProbedMediaInfo FromDto(ProbedInfoDto i) => new(
        new Timecode(i.DurationTicks), i.HasVideo, FromDto(i.FrameRate), i.Width, i.Height, i.HasAudio, i.SampleRate, i.Channels);

    private static Timeline FromDto(TimelineDto t)
    {
        var timeline = new Timeline(FromDto(t.FrameRate), new Resolution(t.Resolution.Width, t.Resolution.Height), t.SampleRate);
        foreach (TrackDto track in t.Tracks)
            timeline.Tracks.Add(FromDto(track));
        return timeline;
    }

    private static Track FromDto(TrackDto t)
    {
        Track track = t.Kind switch
        {
            TrackKind.Video => new VideoTrack { Name = t.Name, Enabled = t.Enabled, Opacity = t.Opacity, BlendMode = t.BlendMode },
            TrackKind.Audio => new AudioTrack { Name = t.Name, Enabled = t.Enabled, GainDb = t.GainDb, Muted = t.Muted, Solo = t.Solo },
            _ => throw new InvalidDataException($"Unknown track kind {t.Kind}."),
        };
        foreach (ClipDto c in t.Clips)
            track.Clips.Add(FromDto(c));
        return track;
    }

    private static Clip FromDto(ClipDto c)
    {
        var clip = new Clip(
            new MediaRefId(c.MediaRefId), new Timecode(c.SourceInTicks), new Timecode(c.SourceOutTicks), new Timecode(c.TimelineStartTicks))
        {
            LinkGroupId = c.LinkGroupId,
        };
        foreach (EffectDto e in c.Effects)
            clip.Effects.Add(FromDto(e));
        return clip;
    }

    private static EffectInstance FromDto(EffectDto e)
    {
        var effect = new EffectInstance(e.EffectTypeId);
        foreach ((string name, AnimatableValueDto value) in e.Parameters)
            effect.Set(name, FromDto(value));
        return effect;
    }

    private static AnimatableValue FromDto(AnimatableValueDto v)
    {
        if (v.Keyframes is { Count: > 0 } keyframes)
            return AnimatableValue.Animated(keyframes.Select(k => new Keyframe(new Timecode(k.TimeTicks), k.Value, k.Interpolation)));
        return AnimatableValue.Constant(v.Constant ?? 0);
    }

    private static Rational FromDto(RationalDto r) => new(r.Num, r.Den);
}
