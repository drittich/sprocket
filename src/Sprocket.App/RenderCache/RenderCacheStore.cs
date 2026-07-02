using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sprocket.App.RenderCache;

/// <summary>One persisted render-cache segment: which scope/sequence/range it covers, the content hash it was
/// rendered under (validity = that hash still matching, ARCHITECTURE.md §20), and its intermediate file.</summary>
public sealed record RenderSegmentRecord(
    string Kind,          // "video" | "audio" (string for a stable, readable manifest)
    Guid SequenceId,
    long InTicks,
    long OutTicks,
    string Hash,
    string FileName,
    int SampleRate = 0,   // audio segments: the cached mix's format
    int Channels = 0);

/// <summary>The render-cache manifest document (a local derived artifact — never committed or merged).</summary>
public sealed record RenderManifestDto(int Version, List<RenderSegmentRecord> Segments);

/// <summary>
/// The on-disk half of the preview render cache (ARCHITECTURE.md §20, PLAN.md step 32): a cache directory
/// <b>beside the project</b> (the NLE convention — Premiere's "Preview Files" folder) holding the rendered
/// intermediates plus a manifest of what ranges they cover. Everything here is local, regenerable, and safely
/// discardable: deleting the directory only forces re-rendering. Untitled projects fall back to a per-user dir
/// (the proxy store family); <c>SPROCKET_RENDER_CACHE_DIR</c> overrides both (tests / portable installs).
/// </summary>
public sealed class RenderCacheStore
{
    private const int ManifestVersion = 1;
    private const string ManifestName = "manifest.json";

    /// <summary>Creates a store rooted at <see cref="DirectoryFor"/>(<paramref name="projectFilePath"/>).</summary>
    public RenderCacheStore(string? projectFilePath) => Directory = DirectoryFor(projectFilePath);

    /// <summary>The cache directory this store reads/writes (not created until something is written).</summary>
    public string Directory { get; }

    /// <summary>
    /// The cache directory for a project: <c>Sprocket Render Files/&lt;project name&gt;</c> beside the project file
    /// (per §20 "a cache dir beside the project"), a per-user <c>renders/untitled</c> dir for unsaved projects, or
    /// the <c>SPROCKET_RENDER_CACHE_DIR</c> override.
    /// </summary>
    public static string DirectoryFor(string? projectFilePath)
    {
        string? overridePath = Environment.GetEnvironmentVariable("SPROCKET_RENDER_CACHE_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        if (!string.IsNullOrEmpty(projectFilePath))
        {
            string fullPath = Path.GetFullPath(projectFilePath);
            string dir = Path.GetDirectoryName(fullPath) ?? ".";
            return Path.Combine(dir, "Sprocket Render Files", Path.GetFileNameWithoutExtension(fullPath));
        }

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, "cache");
        return Path.Combine(baseDir, "Sprocket", "renders", "untitled");
    }

    /// <summary>The absolute path of a segment's intermediate file.</summary>
    public string PathFor(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>A stable, readable file name for a segment: scope + range + a hash prefix + the right extension.</summary>
    public static string FileNameFor(string kind, long inTicks, long outTicks, string hash, string extension) =>
        $"{kind}-{inTicks}-{outTicks}-{hash[..Math.Min(16, hash.Length)]}{extension}";

    /// <summary>Loads the manifest, dropping entries whose intermediate file is missing. An absent/corrupt
    /// manifest reads as empty — the cache is discardable, never load-bearing (§15).</summary>
    public List<RenderSegmentRecord> Load()
    {
        string path = Path.Combine(Directory, ManifestName);
        try
        {
            if (!File.Exists(path))
                return [];
            RenderManifestDto? dto = JsonSerializer.Deserialize<RenderManifestDto>(File.ReadAllText(path));
            if (dto is not { Version: ManifestVersion })
                return [];
            return [.. dto.Segments.Where(s => File.Exists(PathFor(s.FileName)))];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Writes the manifest atomically (temp file + move — the store family's discipline).</summary>
    public void Save(List<RenderSegmentRecord> segments)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, ManifestName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new RenderManifestDto(ManifestVersion, segments),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Total bytes the cache directory currently occupies on disk (0 when absent) — surfaced beside
    /// Delete Render Files so the user can see what deleting reclaims.</summary>
    public long SizeBytes()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return 0;
            long total = 0;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes every file in the cache directory (best-effort — a file still held open by a winding-down decoder
    /// is skipped and swept up by the next <see cref="DeleteOrphans"/>/delete). Returns the bytes reclaimed.
    /// </summary>
    public long DeleteAll()
    {
        long reclaimed = 0;
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return 0;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);
                    reclaimed += size;
                }
                catch
                {
                    // Held open (e.g. the engine's cache decoder mid-teardown) — left for the next sweep.
                }
            }
        }
        catch
        {
            // Enumeration failed (permissions, races) — the cache is discardable; never surface as an error.
        }
        return reclaimed;
    }

    /// <summary>Deletes intermediates no manifest entry references (renders superseded by an edit + re-render, or
    /// files left behind by an earlier failed delete). Best-effort.</summary>
    public void DeleteOrphans(List<RenderSegmentRecord> segments)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;
            var referenced = new HashSet<string>(segments.Select(s => s.FileName), StringComparer.OrdinalIgnoreCase)
            {
                ManifestName,
            };
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                if (!referenced.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); }
                    catch { /* held open — next sweep */ }
                }
            }
        }
        catch
        {
            // Best-effort by design.
        }
    }
}
