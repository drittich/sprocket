using System.IO;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// What one export burn-in line displays (PLAN.md step 29). A burn-in is delivery metadata baked onto the exported
/// frame — the standard set every NLE offers (Premiere's Timecode/Name overlays, Resolve's data burn-in): the
/// running timecode, the topmost clip's name, or a fixed watermark string.
/// </summary>
public enum BurnInField
{
    /// <summary>The running record (timeline) timecode at the frame, formatted <c>HH:MM:SS:FF</c> at the sequence rate.</summary>
    Timecode,

    /// <summary>The name of the topmost content clip covering the frame (media file name, nested-sequence name, …).</summary>
    ClipName,

    /// <summary>A fixed custom string — a watermark / label that does not change frame to frame.</summary>
    Text,
}

/// <summary>
/// Where a burn-in sits on the frame: a nine-point alignment grid, matching the alignment picker professional
/// tools expose for data burn-ins (Resolve/Premiere). The renderer insets each anchor by a small margin so text
/// never touches the frame edge.
/// </summary>
public enum BurnInPosition
{
    /// <summary>Top-left corner.</summary>
    TopLeft,

    /// <summary>Top edge, horizontally centred.</summary>
    TopCenter,

    /// <summary>Top-right corner.</summary>
    TopRight,

    /// <summary>Left edge, vertically centred.</summary>
    MiddleLeft,

    /// <summary>Frame centre.</summary>
    Center,

    /// <summary>Right edge, vertically centred.</summary>
    MiddleRight,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Bottom edge, horizontally centred.</summary>
    BottomCenter,

    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}

/// <summary>
/// One burn-in overlay in an export's burn-in stack (PLAN.md step 29): a <see cref="Field"/> to display at a
/// <see cref="Position"/>. It is pure delivery configuration — plain, serialization-friendly data that flows with
/// the <c>ExportOptions</c> through the export queue. Burn-ins are baked only on the export render (the deterministic
/// §5 render surface), never on the preview hot path, so they never allocate pixels or touch decoded frames (§1).
/// </summary>
/// <param name="Field">What this line shows.</param>
/// <param name="Position">Where on the frame it is anchored.</param>
/// <param name="Text">The literal string for <see cref="BurnInField.Text"/> (watermark); ignored for other fields.</param>
public sealed record BurnIn(
    BurnInField Field,
    BurnInPosition Position = BurnInPosition.BottomLeft,
    string? Text = null);

/// <summary>
/// Resolves a <see cref="BurnIn"/> to the string that should be drawn at a given timeline time, using only the
/// pure model (no rendering). Kept in Core so the resolution — timecode formatting, "which clip is on top" — is
/// unit-testable headlessly; the Render layer only draws the resulting strings (ARCHITECTURE.md §5/§7).
/// </summary>
public static class BurnInResolver
{
    /// <summary>
    /// The display string for <paramref name="item"/> at timeline time <paramref name="t"/> on
    /// <paramref name="sequence"/>, or the empty string when there is nothing to show (e.g. a clip-name burn-in
    /// over a gap). The empty string signals the renderer to skip the line.
    /// </summary>
    public static string Resolve(BurnIn item, Project project, Sequence sequence, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        return item.Field switch
        {
            BurnInField.Timecode => SmpteTimecode.Format(t, sequence.Timeline.FrameRate),
            BurnInField.ClipName => TopmostClipName(project, sequence, t),
            BurnInField.Text => item.Text ?? string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The name of the topmost <em>content</em> clip covering <paramref name="t"/> across the sequence's enabled
    /// video tracks (bottom→top, so a higher track wins) — the media file name, a nested sequence's name, or a
    /// generator's label. Adjustment layers carry no content of their own and are skipped. Returns "" over a gap.
    /// </summary>
    public static string TopmostClipName(Project project, Sequence sequence, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        string name = string.Empty;
        foreach (VideoTrack track in sequence.Timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;
            if (track.ResolveActiveClip(t) is { } clip && clip.Kind != ClipKind.Adjustment
                && ClipName(project, clip) is { Length: > 0 } resolved)
                name = resolved; // a higher (later) track overrides — topmost wins
        }
        return name;
    }

    private static string ClipName(Project project, Clip clip)
    {
        switch (clip.Kind)
        {
            case ClipKind.Media:
                string? path = project.MediaPool.Get(clip.MediaRefId)?.AbsolutePath;
                return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);

            case ClipKind.Sequence:
                return clip.SourceSequenceId is { } id ? project.GetSequence(id)?.Name ?? string.Empty : string.Empty;

            case ClipKind.Generator when clip.Generator is { } gen:
                // A titled generator shows its own text; otherwise its catalog display name ("Color Matte", …).
                string title = gen.GetString(GeneratorParamNames.Text);
                return string.IsNullOrEmpty(title) ? GeneratorCatalog.DisplayName(gen.GeneratorTypeId) : title;

            default:
                return string.Empty;
        }
    }
}
