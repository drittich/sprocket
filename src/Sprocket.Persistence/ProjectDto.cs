using System.Text.Json.Serialization;
using Sprocket.Core.Model;

namespace Sprocket.Persistence;

// The on-disk JSON shape (ARCHITECTURE.md §12). These DTOs are deliberately separate from the domain
// model: the model has constructors, read-only collections, and factory types (AnimatableValue) that don't
// map cleanly onto a serializer, and keeping a distinct wire format lets the model evolve behind a stable,
// versioned file format. Times are stored as raw ticks; enums serialize as strings (stable + readable).

/// <summary>Root document: a schema version plus the whole project.</summary>
internal sealed record ProjectDto(
    int SchemaVersion,
    List<MediaRefDto> Media,
    TimelineDto Timeline,
    SettingsDto Settings);

/// <summary>An imported source. Stores both an absolute path and, when the project path is known, a path
/// relative to the project file so a moved project+media folder still relinks (ARCHITECTURE.md §12).</summary>
internal sealed record MediaRefDto(
    Guid Id,
    string AbsolutePath,
    string? RelativePath,
    ProbedInfoDto Info);

internal sealed record ProbedInfoDto(
    long DurationTicks,
    bool HasVideo,
    RationalDto FrameRate,
    int Width,
    int Height,
    bool HasAudio,
    int SampleRate,
    int Channels);

internal sealed record TimelineDto(
    RationalDto FrameRate,
    ResolutionDto Resolution,
    int SampleRate,
    List<TrackDto> Tracks);

/// <summary>A track in z-order. <see cref="Kind"/> discriminates video vs audio; the unused fields for the
/// other kind are simply ignored (kept flat for a simple, robust format).</summary>
internal sealed record TrackDto(
    TrackKind Kind,
    string Name,
    bool Enabled,
    // Video
    double Opacity,
    BlendMode BlendMode,
    // Audio
    double GainDb,
    bool Muted,
    bool Solo,
    List<ClipDto> Clips);

internal enum TrackKind
{
    Video,
    Audio,
}

internal sealed record ClipDto(
    Guid MediaRefId,
    long SourceInTicks,
    long SourceOutTicks,
    long TimelineStartTicks,
    List<EffectDto> Effects,
    // Linked-clip group (PLAN.md step 13). Null/absent = unlinked; additive + nullable, so v1 files without
    // it load as unlinked and a project with no links serializes byte-identically (WhenWritingNull).
    Guid? LinkGroupId = null);

internal sealed record EffectDto(
    string EffectTypeId,
    Dictionary<string, AnimatableValueDto> Parameters);

/// <summary>An effect parameter: exactly one of <see cref="Constant"/> or <see cref="Keyframes"/> is set.</summary>
internal sealed record AnimatableValueDto(
    double? Constant,
    List<KeyframeDto>? Keyframes);

internal sealed record KeyframeDto(
    long TimeTicks,
    double Value,
    Interpolation Interpolation,
    // Custom Bezier velocity handles (PLAN.md step 16d). Additive + nullable: non-Bezier keyframes omit them
    // (WhenWritingNull), so pre-16d projects serialize byte-identically and still load.
    BezierHandleDto? EaseOut = null,
    BezierHandleDto? EaseIn = null);

internal sealed record BezierHandleDto(double X, double Y);

internal sealed record RationalDto(int Num, int Den);

internal sealed record ResolutionDto(int Width, int Height);

internal sealed record SettingsDto(double MasterGainDb);

/// <summary>Source-generated JSON for the DTO graph: trim/AOT-friendly, enums as strings, indented output.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectDto))]
internal sealed partial class SprocketJsonContext : JsonSerializerContext;
