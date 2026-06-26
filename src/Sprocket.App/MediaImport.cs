using System;
using System.IO;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Media;

namespace Sprocket.App;

/// <summary>
/// Imports a source file into a project's <see cref="MediaPool"/> (PLAN.md step 16b): probes it via
/// <see cref="MediaSource"/> for format/duration and adds a <see cref="MediaRef"/> through the command stack
/// (<see cref="AddMediaCommand"/>), so the import is undoable and flips the dirty indicator (step 10). This is
/// the one place the App's file-import UI touches the Media layer; the bin / thumbnail / badge path (step 15)
/// then lights up for the imported source.
/// </summary>
internal static class MediaImport
{
    /// <summary>
    /// The outcome of an import: the imported (or already-present) <see cref="MediaRef"/> on success, or a
    /// human-readable <see cref="Error"/> explaining why the file could not be opened.
    /// </summary>
    public readonly record struct Result(MediaRef? Media, string? Error)
    {
        public bool Succeeded => Media is not null;
    }

    /// <summary>
    /// Probes <paramref name="path"/> and adds it to the project (deduplicating by absolute path — re-importing
    /// the same file returns the existing reference rather than a second copy). Returns a <see cref="Result"/>
    /// carrying the imported/existing <see cref="MediaRef"/>, or the failure reason (the underlying FFmpeg
    /// message) when the file can't be opened/probed — so the caller can show *why* rather than a bare "failed".
    /// </summary>
    public static Result TryImport(Project project, EditHistory history, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(path))
            return new Result(null, "No file path.");
        if (!File.Exists(path))
            return new Result(null, "File not found.");

        // Already imported? Match on the stored absolute path so a file isn't added twice.
        foreach (MediaRef existing in project.MediaPool.Items)
            if (string.Equals(existing.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
                return new Result(existing, null);

        ProbedMediaInfo info;
        try
        {
            using MediaSource probe = MediaSource.Open(path);
            info = probe.Info;
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message); // surfaced to the user (§15)
        }

        var media = new MediaRef(MediaRefId.New(), path, info);
        history.Execute(new AddMediaCommand(project.MediaPool, media));
        return new Result(media, null);
    }
}
