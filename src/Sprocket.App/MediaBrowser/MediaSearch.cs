using System;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// The media-bin search filter (PLAN.md step 15, UI.md §3.3): a case-insensitive substring match used to filter
/// the bin as the user types. Pure so the filtering rule is unit-testable independently of the UI.
/// </summary>
public static class MediaSearch
{
    /// <summary>
    /// Whether <paramref name="text"/> matches <paramref name="query"/>. An empty or whitespace query matches
    /// everything (the unfiltered bin); otherwise it is a case-insensitive substring test.
    /// </summary>
    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (string.IsNullOrEmpty(text))
            return false;
        return text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
