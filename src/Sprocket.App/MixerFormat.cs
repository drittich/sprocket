namespace Sprocket.App;

/// <summary>
/// Pure formatting for the audio mixer / loudness meters (PLAN.md step 30, UI.md §3.3): gain and pan labels,
/// LUFS / dBTP read-outs, and the 0–1 fill fraction that drives a meter bar. Kept free of any Avalonia control —
/// like <see cref="StatusBarFormat"/> / <see cref="MarkerListFormat"/> — so the strings/fractions are unit-testable
/// and the mixer control only maps them onto widgets.
/// </summary>
public static class MixerFormat
{
    private const string NegInf = "-∞"; // silence sentinel

    /// <summary>A gain in dB as a signed, one-decimal label (e.g. <c>"+3.0 dB"</c>, <c>"-6.0 dB"</c>,
    /// <c>"0.0 dB"</c>); values at/below -60 dB read as <c>"-∞ dB"</c> (fader floor = silence).</summary>
    public static string GainDbLabel(double db)
    {
        if (double.IsNegativeInfinity(db) || db <= -60.0)
            return $"{NegInf} dB";
        return $"{Signed(db)} dB";
    }

    /// <summary>A pan/balance value in [-1, 1] as <c>"C"</c> (centre), <c>"L100".."L1"</c> (left) or
    /// <c>"R1".."R100"</c> (right).</summary>
    public static string PanLabel(double pan)
    {
        pan = Math.Clamp(pan, -1.0, 1.0);
        int pct = (int)Math.Round(Math.Abs(pan) * 100);
        if (pct == 0) return "C";
        return pan < 0 ? $"L{pct}" : $"R{pct}";
    }

    /// <summary>A loudness value as a one-decimal LUFS label (<c>"-14.2 LUFS"</c>), or <c>"-∞ LUFS"</c> for
    /// silence / not-yet-measured.</summary>
    public static string LufsLabel(double lufs) =>
        double.IsNegativeInfinity(lufs) || double.IsNaN(lufs) ? $"{NegInf} LUFS" : $"{lufs:0.0} LUFS";

    /// <summary>A true-peak value as a one-decimal dBTP label (<c>"-1.0 dBTP"</c>), or <c>"-∞ dBTP"</c> for
    /// silence.</summary>
    public static string DbtpLabel(double dbtp) =>
        double.IsNegativeInfinity(dbtp) || double.IsNaN(dbtp) ? $"{NegInf} dBTP" : $"{dbtp:0.0} dBTP";

    /// <summary>
    /// Maps a level in dB(FS) to a 0–1 meter fill between <paramref name="floorDb"/> (0) and
    /// <paramref name="ceilingDb"/> (1); silence and anything at/below the floor is 0, anything at/above the
    /// ceiling is 1.
    /// </summary>
    public static double MeterFillFraction(double db, double floorDb = -60.0, double ceilingDb = 0.0)
    {
        if (double.IsNegativeInfinity(db) || double.IsNaN(db) || db <= floorDb) return 0.0;
        if (db >= ceilingDb) return 1.0;
        return (db - floorDb) / (ceilingDb - floorDb);
    }

    private static string Signed(double value)
    {
        // Round to one decimal first so a tiny negative doesn't render as "-0.0".
        double r = Math.Round(value, 1);
        return (r > 0 ? "+" : r < 0 ? "-" : "") + Math.Abs(r).ToString("0.0");
    }
}
