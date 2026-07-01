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
    // The active sequence's timeline — the single-sequence (and pre-step-23) shape. Null when the multi-sequence
    // <see cref="Sequences"/> shape is used instead.
    TimelineDto? Timeline,
    SettingsDto Settings,
    // Multiple sequences + nesting (PLAN.md step 23). Additive + nullable: a single-sequence project with no
    // nested-sequence clips writes only Timeline (byte-identical to pre-23 files, no schema bump); a multi-sequence
    // or nested project writes all Sequences + ActiveSequenceId instead, and omits Timeline.
    List<SequenceDto>? Sequences = null,
    Guid? ActiveSequenceId = null,
    // Synced multicam angle groups (PLAN.md step 24). Additive + nullable and orthogonal to the sequence shape:
    // a project with no multicam sources writes null (WhenWritingNull), so pre-24 files load with none and a
    // multicam-free project serializes byte-identically.
    List<MulticamSourceDto>? MulticamSources = null);

/// <summary>A synced multicam source (PLAN.md step 24): a stable id, a display name, and its camera angles.</summary>
internal sealed record MulticamSourceDto(
    Guid Id,
    string Name,
    List<MulticamAngleDto> Angles);

/// <summary>One camera angle: its display name, the source it draws video from, the sync offset that aligns it
/// with the other angles, and an optional separate audio source (null = audio from the video file).</summary>
internal sealed record MulticamAngleDto(
    string Name,
    Guid MediaRefId,
    long SyncOffsetTicks,
    Guid? AudioMediaRefId = null);

/// <summary>A named sequence (PLAN.md step 23): a stable id, a display name, and its timeline content.</summary>
internal sealed record SequenceDto(
    Guid Id,
    string Name,
    TimelineDto Timeline);

/// <summary>An imported source, referenced by stable <see cref="Id"/> (ARCHITECTURE.md §12, PLAN.md step 28).
/// The content-derived <see cref="Info"/> is part of the shared, diffable project — every collaborator needs it
/// to edit/render (even offline) and it never varies per user. The per-user asset <b>paths</b>, by contrast, live
/// in a separate <see cref="MediaLinksDto">media-link sidecar</see> so a pulled project-file change never forces a
/// relink. <see cref="AbsolutePath"/>/<see cref="RelativePath"/> are therefore optional/nullable: the collab-ready
/// <see cref="ProjectSerializer.Save"/> omits them (paths go to the sidecar), while the self-contained
/// <see cref="ProjectSerializer.Serialize"/> string (autosave/snapshots) and pre-step-28 files inline them — both
/// are still read on load (inline paths win only when no sidecar entry exists).</summary>
internal sealed record MediaRefDto(
    Guid Id,
    ProbedInfoDto Info,
    string? AbsolutePath = null,
    string? RelativePath = null);

/// <summary>
/// The per-user media-link sidecar (PLAN.md step 28, ARCHITECTURE.md §12): the mapping from a source's stable
/// <see cref="MediaRefDto.Id"/> to its local path on <em>this</em> machine. Kept out of the shared project file
/// (and not normally committed / merged) so pulling a collaborator's edit never relocates your own clips — your
/// link file still resolves the ids. Missing entries (a fresh clone with no sidecar) simply leave that source
/// offline until relinked (PLAN.md step 28 batch relink, §15). Independently schema-versioned from the project.
/// </summary>
internal sealed record MediaLinksDto(
    int SchemaVersion,
    List<MediaLinkDto> Links);

/// <summary>One id→path link. Stores both an absolute path and, when the project path is known, a path relative to
/// the project file so moving the whole project folder (project + sidecar + media) still relinks.</summary>
internal sealed record MediaLinkDto(
    Guid Id,
    string AbsolutePath,
    string? RelativePath = null);

internal sealed record ProbedInfoDto(
    long DurationTicks,
    bool HasVideo,
    RationalDto FrameRate,
    int Width,
    int Height,
    bool HasAudio,
    int SampleRate,
    int Channels,
    // Alpha-channel flag (PLAN.md step 26). Additive + nullable: an opaque source writes null (WhenWritingNull),
    // so pre-26 files load with no alpha and opaque media serializes byte-identically. Only alpha media writes true.
    bool? HasAlpha = null,
    // Source format details probed at import (PLAN.md step 27). All additive + nullable so a pre-27 file loads with
    // sensible defaults and the informational fields (recomputed on the next probe) don't bloat the diff.
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? PixelFormatName = null,
    int? BitDepth = null,
    bool? IsHdr = null,
    bool? IsVariableFrameRate = null);

internal sealed record TimelineDto(
    RationalDto FrameRate,
    ResolutionDto Resolution,
    int SampleRate,
    List<TrackDto> Tracks,
    // Sequence markers (PLAN.md step 20). Additive + nullable: a marker-less timeline writes null (WhenWritingNull)
    // so pre-20 files load with no markers and a project with none serializes byte-identically.
    List<MarkerDto>? Markers = null);

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
    List<ClipDto> Clips,
    // Transitions on the track's cuts (PLAN.md step 25). Additive + nullable: a track with no transitions writes
    // null (WhenWritingNull), so pre-25 files load with none and a transition-free project serializes byte-identically.
    List<TransitionDto>? Transitions = null,
    // Audio track stereo balance (PLAN.md step 30). Additive + nullable: a centred track writes null
    // (WhenWritingNull), so pre-30 files load centred and un-panned projects serialize byte-identically.
    double? Pan = null);

/// <summary>A transition on a cut (PLAN.md step 25): its type id, the cut it sits on, its length, alignment, and
/// any type parameters (null when there are none — the v1 built-ins).</summary>
internal sealed record TransitionDto(
    string TransitionTypeId,
    long CutPointTicks,
    long DurationTicks,
    TransitionAlignment Alignment,
    Dictionary<string, AnimatableValueDto>? Parameters = null);

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
    Guid? LinkGroupId = null,
    // Clip kind + generator (PLAN.md step 19). Additive + nullable: a plain media clip writes neither (Kind null
    // ⇒ Media on load), so pre-19 files load unchanged and media-only projects serialize byte-identically.
    ClipKind? Kind = null,
    GeneratorDto? Generator = null,
    // Clip markers (PLAN.md step 20). Additive + nullable: a marker-less clip writes null (WhenWritingNull).
    List<MarkerDto>? Markers = null,
    // Playback speed (retime, PLAN.md step 21). Additive + nullable: a normal-speed (1/1) clip writes neither
    // (WhenWritingNull), so pre-21 files load at 1× and un-retimed projects serialize byte-identically.
    int? SpeedNum = null,
    int? SpeedDen = null,
    // Nested-sequence source (PLAN.md step 23). Present only on a Kind == Sequence clip; null/absent otherwise,
    // so non-nesting projects serialize byte-identically (WhenWritingNull).
    Guid? SourceSequenceId = null,
    // Multicam source + active angle (PLAN.md step 24). Present only on a Kind == Multicam clip; null/absent
    // otherwise, so non-multicam projects serialize byte-identically (WhenWritingNull).
    Guid? SourceMulticamId = null,
    int? ActiveAngle = null,
    // Per-clip audio gain in dB (PLAN.md step 30). Additive + nullable: unity (0 dB) writes null (WhenWritingNull),
    // so pre-30 files load at unity and un-gained projects serialize byte-identically.
    double? GainDb = null);

/// <summary>A marker (PLAN.md step 20): a time, optional name/comment, colour band, and an optional span
/// (<see cref="DurationTicks"/> &gt; 0). Colour serializes as a string enum.</summary>
internal sealed record MarkerDto(
    long TimeTicks,
    string Name,
    string Comment,
    MarkerColor Color,
    long DurationTicks);

/// <summary>A generator clip's procedural source (PLAN.md step 19): a type id, string parameters (text, colour
/// hex), and numeric animatable parameters. Present only on generator clips.</summary>
internal sealed record GeneratorDto(
    string GeneratorTypeId,
    Dictionary<string, string> Strings,
    Dictionary<string, AnimatableValueDto> Parameters);

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

/// <summary>Project settings. <see cref="UseProxies"/>/<see cref="ProxyTier"/> are additive (PLAN.md step 18):
/// they carry constructor defaults so pre-18 files (which omit them) load with proxies on at the Half tier, and a
/// project at those defaults still serializes them explicitly — the defaults just guarantee a safe load.</summary>
internal sealed record SettingsDto(
    double MasterGainDb,
    bool UseProxies = true,
    ProxyTier ProxyTier = ProxyTier.Half);

/// <summary>Source-generated JSON for the DTO graph: trim/AOT-friendly, enums as strings, indented output.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(MediaLinksDto))]
internal sealed partial class SprocketJsonContext : JsonSerializerContext;
