namespace Sprocket.Core.Audio;

/// <summary>
/// Pure loudness-normalization math (PLAN.md step 30): turn a measured loudness (and true peak) into the model
/// gain adjustment that brings a clip / track / master to a target integrated loudness, without exceeding a
/// true-peak ceiling. The measurement itself (K-weighting, gating) lives in <c>Sprocket.Audio</c>; applying the
/// gain is an undoable model edit — so this helper stays a dependency-free function of numbers.
/// </summary>
public static class LoudnessNormalization
{
    /// <summary>Streaming target used by YouTube / Spotify and most platforms (−14 LUFS).</summary>
    public const double StreamingMinus14Lufs = -14.0;

    /// <summary>Streaming target used by Apple / AES-recommended delivery (−16 LUFS).</summary>
    public const double StreamingMinus16Lufs = -16.0;

    /// <summary>Broadcast target of EBU R128 (−23 LUFS).</summary>
    public const double BroadcastMinus23Lufs = -23.0;

    /// <summary>The conventional delivery true-peak ceiling (−1 dBTP) — headroom against downstream clipping.</summary>
    public const double DefaultTruePeakCeilingDbtp = -1.0;

    /// <summary>
    /// The gain (in dB) to apply so a signal measured at <paramref name="measuredLufs"/> reaches
    /// <paramref name="targetLufs"/>, reduced if necessary so its true peak (<paramref name="measuredTruePeakDbtp"/>
    /// after gain) does not exceed <paramref name="truePeakCeilingDbtp"/>. Silence / unmeasurable input returns 0
    /// (nothing to normalize). True-peak limiting only ever reduces the gain below the loudness target.
    /// </summary>
    public static double ComputeGainDb(
        double measuredLufs,
        double measuredTruePeakDbtp,
        double targetLufs,
        double truePeakCeilingDbtp = DefaultTruePeakCeilingDbtp)
    {
        if (double.IsNegativeInfinity(measuredLufs) || double.IsNaN(measuredLufs))
            return 0.0; // silence / not measurable — leave the level alone

        double loudnessGainDb = targetLufs - measuredLufs;

        if (double.IsNegativeInfinity(measuredTruePeakDbtp) || double.IsNaN(measuredTruePeakDbtp))
            return loudnessGainDb; // no peak information — apply the loudness gain as-is

        // A gain of g dB raises the true peak to measured + g; cap g so that stays at/under the ceiling.
        double peakHeadroomDb = truePeakCeilingDbtp - measuredTruePeakDbtp;
        return Math.Min(loudnessGainDb, peakHeadroomDb);
    }
}
