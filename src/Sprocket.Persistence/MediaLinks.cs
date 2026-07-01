using System.Text.Json;
using Sprocket.Core.Model;

namespace Sprocket.Persistence;

/// <summary>
/// The per-user <b>media-link sidecar</b> (PLAN.md step 28, ARCHITECTURE.md §12): the mapping from each source's
/// stable <see cref="MediaRefId"/> to its local path on <em>this</em> machine, stored beside the project file but
/// kept <b>out of the shared, diffable project</b> (and not normally committed or merged). This is the
/// collaboration-ready format split — the project JSON references sources by id only, so pulling a collaborator's
/// project-file change never forces you to relocate your own clips: your link file still resolves the ids.
/// </summary>
/// <remarks>
/// The sidecar is <b>optional and best-effort</b> by design. A project opened without one (a fresh clone, or a
/// project shared without link files) simply loads every source offline — rendered as black/silence (§15) and
/// surfaced in the missing-media list the batch-relink workflow drives (PLAN.md step 28). Writing is atomic
/// (temp file → promote), like the autosave / proxy / render-cache stores, so a crash mid-write never corrupts it.
/// </remarks>
public static class MediaLinks
{
    /// <summary>The media-link sidecar suffix appended to a project file path.</summary>
    public const string Suffix = ".links.json";

    /// <summary>The current sidecar schema version (independent of the project schema).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The media-link sidecar path for a project file (e.g. <c>foo.sprocket.json.links.json</c>).</summary>
    public static string SidecarPath(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectFilePath);
        return projectFilePath + Suffix;
    }

    /// <summary>
    /// Atomically writes the project's media-link sidecar beside <paramref name="projectFilePath"/>, recording each
    /// source's absolute path plus (when it shares a root with the project) a project-relative path so moving the
    /// whole folder still relinks. A media ref with an empty path (an unresolved / offline source) is skipped — it
    /// simply has no local link on this machine yet. Because paths are per-user, this can be written independently
    /// of the project's dirty state (e.g. right after a relink) without touching the shared project file.
    /// </summary>
    public static void Write(Project project, string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(projectFilePath);

        string sidecar = SidecarPath(projectFilePath);
        WriteText(Serialize(project, projectFilePath), sidecar);
    }

    /// <summary>Serializes the project's media links to JSON, computing project-relative paths against
    /// <paramref name="projectFilePath"/>'s directory. Exposed for testing and for callers that stage the write.</summary>
    public static string Serialize(Project project, string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(projectFilePath);

        string? projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var links = new List<MediaLinkDto>();
        foreach (MediaRef m in project.MediaPool.Items)
        {
            if (string.IsNullOrEmpty(m.AbsolutePath))
                continue; // offline / not-yet-linked on this machine — nothing to record
            links.Add(new MediaLinkDto(m.Id.Value, m.AbsolutePath, RelativeTo(projectDir, m.AbsolutePath)));
        }
        var dto = new MediaLinksDto(SchemaVersion, links);
        return JsonSerializer.Serialize(dto, SprocketJsonContext.Default.MediaLinksDto);
    }

    /// <summary>
    /// Reads the media-link sidecar beside <paramref name="projectFilePath"/> if present, returning the resolved
    /// id→path map (relative path preferred when it exists on disk, else the stored absolute path). Returns an
    /// empty map when the sidecar is absent or unreadable — the project then loads offline (§15). Never throws for
    /// a missing sidecar; that is the normal fresh-clone case.
    /// </summary>
    public static IReadOnlyDictionary<MediaRefId, string> Read(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectFilePath);
        string sidecar = SidecarPath(projectFilePath);
        if (!File.Exists(sidecar))
            return EmptyMap;
        string? projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        try
        {
            return Deserialize(File.ReadAllText(sidecar), projectDir);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt / unreadable sidecar must not fail the load — treat it as "no links" (offline, relinkable).
            return EmptyMap;
        }
    }

    /// <summary>Parses a media-link sidecar JSON string into the resolved id→path map. <paramref name="projectDir"/>,
    /// when given, resolves relative paths and is preferred when the resolved file exists.</summary>
    public static IReadOnlyDictionary<MediaRefId, string> Deserialize(string json, string? projectDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        MediaLinksDto? dto = JsonSerializer.Deserialize(json, SprocketJsonContext.Default.MediaLinksDto);
        if (dto is null)
            return EmptyMap;

        var map = new Dictionary<MediaRefId, string>();
        foreach (MediaLinkDto link in dto.Links)
            map[new MediaRefId(link.Id)] = ResolvePath(link, projectDir);
        return map;
    }

    /// <summary>Deletes the media-link sidecar if present. Silently ignores a missing file / directory.</summary>
    public static void Delete(string projectFilePath)
    {
        string sidecar = SidecarPath(projectFilePath);
        try
        {
            File.Delete(sidecar);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to delete.
        }
    }

    private static readonly IReadOnlyDictionary<MediaRefId, string> EmptyMap =
        new Dictionary<MediaRefId, string>();

    /// <summary>Prefer the relative path resolved against the project directory when that file exists; otherwise
    /// fall back to the stored absolute path (which may be offline — tolerated, §12).</summary>
    private static string ResolvePath(MediaLinkDto link, string? projectDir)
    {
        if (projectDir is not null && link.RelativePath is not null)
        {
            string candidate = Path.GetFullPath(Path.Combine(projectDir, link.RelativePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return link.AbsolutePath;
    }

    /// <summary>A path relative to <paramref name="projectDir"/>, or <see langword="null"/> when there is no shared
    /// root (e.g. a different drive) so only a genuinely relative location is stored.</summary>
    private static string? RelativeTo(string? projectDir, string absolutePath)
    {
        if (projectDir is null)
            return null;
        string rel = Path.GetRelativePath(projectDir, absolutePath);
        // GetRelativePath returns the input unchanged when there's no shared root; only store an actual relative path.
        return rel != absolutePath && !Path.IsPathRooted(rel) ? rel : null;
    }

    private static void WriteText(string json, string sidecarPath)
    {
        string tempPath = sidecarPath + ".tmp";
        File.WriteAllText(tempPath, json);
        // Move-with-overwrite is atomic on the same volume — a reader sees the old or the new file, never a partial.
        File.Move(tempPath, sidecarPath, overwrite: true);
    }
}
