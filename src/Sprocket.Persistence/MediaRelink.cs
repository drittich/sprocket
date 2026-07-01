using Sprocket.Core.Model;

namespace Sprocket.Persistence;

/// <summary>An offline source that needs relinking: its stable id and its last-known (now-stale or empty) path.</summary>
/// <param name="Id">The source's id in the <see cref="MediaPool"/>.</param>
/// <param name="OriginalPath">The path recorded for it (from this machine's media-link sidecar), which no longer
/// resolves — empty when the source was never linked here (a collaborator's clip with no local sidecar entry).</param>
public readonly record struct OfflineMedia(MediaRefId Id, string OriginalPath)
{
    /// <summary>The bare file name of the original path (empty when there is no path to match on).</summary>
    public string FileName => string.IsNullOrEmpty(OriginalPath) ? string.Empty : Path.GetFileName(OriginalPath);
}

/// <summary>A candidate replacement file discovered under a relink root folder.</summary>
/// <param name="Path">Absolute path to the candidate.</param>
/// <param name="SizeBytes">Its size in bytes (a tie-breaker; 0 when unknown).</param>
public readonly record struct MediaCandidate(string Path, long SizeBytes)
{
    /// <summary>The candidate's bare file name.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>A confident relink: point <paramref name="Id"/> from its stale <paramref name="OriginalPath"/> at
/// <paramref name="NewPath"/>.</summary>
public readonly record struct RelinkMatch(MediaRefId Id, string OriginalPath, string NewPath);

/// <summary>
/// The result of matching a set of offline sources against a folder of candidates (PLAN.md step 28): the confident
/// <see cref="Matches"/> ready to apply, the <see cref="Ambiguous"/> ones (several equally-good candidates — the
/// user must choose), and the <see cref="Unmatched"/> ones (no candidate found). Nothing is applied until the user
/// confirms; ambiguous/unmatched are reported, never silently guessed.
/// </summary>
public sealed record RelinkPlan(
    IReadOnlyList<RelinkMatch> Matches,
    IReadOnlyList<OfflineMedia> Ambiguous,
    IReadOnlyList<OfflineMedia> Unmatched);

/// <summary>
/// Pure matcher for the batch-relink workflow (PLAN.md step 28), separated from file I/O so it is unit-testable
/// headlessly. Given the offline sources (each with its stale original path) and a set of candidate files under a
/// chosen root folder, it proposes replacements by <b>file name</b>, disambiguating multiple same-named candidates
/// by the <b>longest matching path tail</b> (so <c>…/media/clip.mp4</c> beats <c>…/other/clip.mp4</c> when the
/// original lived under <c>media</c>) and, failing that, by <b>file size</b> when the original size is known.
/// </summary>
public static class MediaRelinkMatcher
{
    /// <summary>Matches <paramref name="offline"/> against <paramref name="candidates"/>. <paramref name="originalSizes"/>,
    /// when supplied, gives an offline source's known original size (bytes) as a final tie-breaker.</summary>
    public static RelinkPlan Match(
        IReadOnlyList<OfflineMedia> offline,
        IReadOnlyList<MediaCandidate> candidates,
        IReadOnlyDictionary<MediaRefId, long>? originalSizes = null)
    {
        ArgumentNullException.ThrowIfNull(offline);
        ArgumentNullException.ThrowIfNull(candidates);

        // Index candidates by file name (case-insensitive — Windows/macOS are case-insensitive, and matching by
        // name across a moved tree is the common case even on Linux).
        var byName = new Dictionary<string, List<MediaCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (MediaCandidate c in candidates)
        {
            if (!byName.TryGetValue(c.FileName, out List<MediaCandidate>? list))
                byName[c.FileName] = list = new List<MediaCandidate>();
            list.Add(c);
        }

        var matches = new List<RelinkMatch>();
        var ambiguous = new List<OfflineMedia>();
        var unmatched = new List<OfflineMedia>();

        foreach (OfflineMedia target in offline)
        {
            string name = target.FileName;
            if (name.Length == 0 || !byName.TryGetValue(name, out List<MediaCandidate>? group))
            {
                unmatched.Add(target);
                continue;
            }

            if (group.Count == 1)
            {
                matches.Add(new RelinkMatch(target.Id, target.OriginalPath, group[0].Path));
                continue;
            }

            long? size = originalSizes is not null && originalSizes.TryGetValue(target.Id, out long s) ? s : null;
            MediaCandidate? best = Disambiguate(target.OriginalPath, size, group);
            if (best is { } chosen)
                matches.Add(new RelinkMatch(target.Id, target.OriginalPath, chosen.Path));
            else
                ambiguous.Add(target);
        }

        return new RelinkPlan(matches, ambiguous, unmatched);
    }

    /// <summary>Picks the single best candidate among same-named ones, or <see langword="null"/> when two remain
    /// genuinely tied (equal path-tail length and, if known, equal size) — that is reported as ambiguous.</summary>
    private static MediaCandidate? Disambiguate(string originalPath, long? originalSize, List<MediaCandidate> group)
    {
        // 1) Longest matching path tail (number of trailing path segments in common with the original).
        int bestTail = -1;
        var byTail = new List<MediaCandidate>();
        foreach (MediaCandidate c in group)
        {
            int tail = CommonTailSegments(originalPath, c.Path);
            if (tail > bestTail)
            {
                bestTail = tail;
                byTail.Clear();
                byTail.Add(c);
            }
            else if (tail == bestTail)
            {
                byTail.Add(c);
            }
        }
        if (byTail.Count == 1)
            return byTail[0];

        // 2) Break a path-tail tie by exact size match, when the original size is known.
        if (originalSize is { } want)
        {
            var sized = byTail.Where(c => c.SizeBytes == want).ToList();
            if (sized.Count == 1)
                return sized[0];
        }

        return null; // genuinely ambiguous
    }

    /// <summary>The number of trailing path segments (file name inclusive) two paths share, compared case-insensitively.
    /// Both paths are split on either separator so a Windows-recorded path matches a POSIX candidate and vice versa.</summary>
    public static int CommonTailSegments(string a, string b)
    {
        string[] sa = Split(a);
        string[] sb = Split(b);
        int i = sa.Length - 1, j = sb.Length - 1, count = 0;
        while (i >= 0 && j >= 0 && string.Equals(sa[i], sb[j], StringComparison.OrdinalIgnoreCase))
        {
            count++;
            i--;
            j--;
        }
        return count;
    }

    private static string[] Split(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The file-I/O half of the batch-relink workflow (PLAN.md step 28): find a project's offline sources, scan a
/// chosen root folder for candidate files, and apply a <see cref="RelinkPlan"/> back onto the model. Relinking
/// changes only a source's <b>local path</b> — a per-user concern that lives in the media-link sidecar
/// (<see cref="MediaLinks"/>), not the shared, diffable project — so it is a direct <see cref="MediaRef.AbsolutePath"/>
/// update rather than an undoable editorial edit; persist it with <see cref="MediaLinks.Write"/> or a normal save.
/// </summary>
public static class MediaRelink
{
    /// <summary>The project's offline sources: those with no local path, or whose recorded path no longer resolves
    /// to a file on disk (§15). These are what the relink dialog lists as "missing media".</summary>
    public static IReadOnlyList<OfflineMedia> FindOffline(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var offline = new List<OfflineMedia>();
        foreach (MediaRef m in project.MediaPool.Items)
            if (IsOffline(m.AbsolutePath))
                offline.Add(new OfflineMedia(m.Id, m.AbsolutePath));
        return offline;
    }

    /// <summary>Whether a source path is offline: empty (never linked here) or missing on disk (moved / deleted).</summary>
    public static bool IsOffline(string? absolutePath) =>
        string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath);

    /// <summary>
    /// Enumerates candidate files under <paramref name="root"/> (recursively by default), each with its size.
    /// Inaccessible sub-directories are skipped rather than aborting the scan. Returns an empty list when the root
    /// doesn't exist.
    /// </summary>
    public static IReadOnlyList<MediaCandidate> ScanFolder(string root, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var candidates = new List<MediaCandidate>();
        if (!Directory.Exists(root))
            return candidates;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    long size = 0;
                    try { size = new FileInfo(file).Length; }
                    catch (IOException) { /* size is only a tie-breaker; leave it 0 */ }
                    catch (UnauthorizedAccessException) { }
                    candidates.Add(new MediaCandidate(file, size));
                }
                if (recursive)
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                        stack.Push(sub);
            }
            catch (UnauthorizedAccessException) { /* skip a folder we can't read */ }
            catch (DirectoryNotFoundException) { /* raced deletion — skip */ }
        }
        return candidates;
    }

    /// <summary>Convenience: find the project's offline sources and match them against <paramref name="root"/>'s
    /// files in one call, producing a plan to preview before applying.</summary>
    public static RelinkPlan Plan(Project project, string root)
    {
        ArgumentNullException.ThrowIfNull(project);
        return MediaRelinkMatcher.Match(FindOffline(project), ScanFolder(root));
    }

    /// <summary>Applies a plan's confident <see cref="RelinkPlan.Matches"/> to the project, re-pointing each matched
    /// source at its new path. Returns the number of sources relinked. Ambiguous/unmatched entries are left offline.</summary>
    public static int Apply(Project project, RelinkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        int relinked = 0;
        foreach (RelinkMatch match in plan.Matches)
        {
            if (project.MediaPool.Get(match.Id) is { } media)
            {
                media.AbsolutePath = match.NewPath;
                relinked++;
            }
        }
        return relinked;
    }
}
