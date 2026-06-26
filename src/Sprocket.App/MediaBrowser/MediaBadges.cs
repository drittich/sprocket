using System;
using System.Collections.Generic;
using System.IO;
using Sprocket.Core.Model;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Derives the small metadata badges shown on a media-bin tile (PLAN.md step 15, UI.md §3.3) — duration and a
/// resolution tier for video (<c>4K · 1080p · 720p</c>) or a format tag for audio (<c>WAV · AIF</c>). Pure so
/// it is unit-testable without a UI; the tile control just renders the strings. Alpha-channel detection
/// (<c>Alpha</c> in the mockup) lands with the premultiplied-alpha path (PLAN.md step 20); the slot is left for it.
/// </summary>
public static class MediaBadges
{
    /// <summary>Builds the ordered badge strings for a source. The first is always the duration.</summary>
    public static IReadOnlyList<string> Describe(ProbedMediaInfo info, string path)
    {
        ArgumentNullException.ThrowIfNull(info);
        var badges = new List<string> { Duration(info.Duration.ToSeconds()) };

        if (info.HasVideo)
            badges.Add(ResolutionTier(info.Width, info.Height));
        else if (info.HasAudio)
            badges.Add(FormatTag(path));

        return badges;
    }

    /// <summary>A coarse, human-friendly resolution label: <c>4K</c>, <c>1080p</c>, <c>720p</c>, else <c>W×H</c>.</summary>
    public static string ResolutionTier(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "—";
        if (height >= 2160 || width >= 3840)
            return "4K";
        if (height >= 1080)
            return "1080p";
        if (height >= 720)
            return "720p";
        return $"{width}×{height}";
    }

    /// <summary>The file extension as an upper-case tag (<c>.wav</c> → <c>WAV</c>), or <c>AUDIO</c> if there is none.</summary>
    public static string FormatTag(string path)
    {
        string ext = Path.GetExtension(path ?? string.Empty).TrimStart('.');
        return ext.Length == 0 ? "AUDIO" : ext.ToUpperInvariant();
    }

    /// <summary>Formats a duration in seconds as <c>m:ss</c> (e.g. 22.4 → <c>0:22</c>, 96 → <c>1:36</c>).</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds))
            seconds = 0;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:00}";
    }
}
