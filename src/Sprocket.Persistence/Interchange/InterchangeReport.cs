namespace Sprocket.Persistence.Interchange;

/// <summary>
/// The lossy-conversion report produced by an interchange export or import (PLAN.md step 28). Interchange formats
/// (EDL, Final Cut XML) can carry only a subset of Sprocket's model, so anything that cannot be represented is
/// <b>reported, not silently dropped</b> — the caller surfaces these so the user knows exactly what did and did not
/// travel. Messages are aggregated by category (e.g. "12 clip effects dropped") rather than one line per item.
/// </summary>
public sealed class InterchangeReport
{
    private readonly List<string> _warnings = new();
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>The distinct warnings, in first-seen order (counted categories rendered as a single summary line).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var lines = new List<string>(_warnings);
            foreach ((string category, int count) in _counts)
                lines.Add(count == 1 ? $"{category} (1 item)" : $"{category} ({count} items)");
            return lines;
        }
    }

    /// <summary>Whether anything was lost in the conversion.</summary>
    public bool HasWarnings => _warnings.Count > 0 || _counts.Count > 0;

    /// <summary>Records a one-off warning verbatim.</summary>
    public void Warn(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        _warnings.Add(message);
    }

    /// <summary>Records one occurrence of a countable lossy <paramref name="category"/> (e.g. "Clip effects dropped").
    /// Repeated categories accumulate into a single summarised line.</summary>
    public void Count(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        _counts[category] = _counts.GetValueOrDefault(category) + 1;
    }

    /// <summary>A human-readable multi-line summary (empty string when nothing was lost).</summary>
    public override string ToString() => string.Join(Environment.NewLine, Warnings);
}

/// <summary>An interchange <b>export</b> result: the produced document text and its lossy-conversion report.</summary>
public sealed record InterchangeExport(string Text, InterchangeReport Report);
