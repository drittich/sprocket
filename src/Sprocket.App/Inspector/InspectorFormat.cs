using System.Globalization;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure formatting for the Inspector (PLAN.md step 16), split out like <see cref="Timeline.TimelineMath"/> so
/// the value-display logic is unit-testable without an Avalonia surface (the App is a UI-bound WinExe).
/// </summary>
public static class InspectorFormat
{
    /// <summary>
    /// Formats a parameter value for display: up to three decimals, trailing zeros trimmed, with an optional
    /// unit suffix (degrees abut the number; other units are spaced, e.g. <c>"+1 EV"</c> style — sign is the
    /// caller's value, we don't force a <c>+</c>).
    /// </summary>
    public static string Value(double value, string? unit = null)
    {
        string number = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(unit))
            return number;
        return unit == "°" ? $"{number}{unit}" : $"{number} {unit}";
    }
}
