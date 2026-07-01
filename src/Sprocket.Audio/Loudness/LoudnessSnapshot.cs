namespace Sprocket.Audio.Loudness;

/// <summary>
/// An immutable read-out of the loudness meter at one instant (EBU R128 / ITU-R BS.1770-4). All loudness values
/// are in LUFS (== LKFS) and <see cref="TruePeakDbtp"/> in dBTP; a value of <see cref="double.NegativeInfinity"/>
/// means "silence / not yet measured". Produced on the UI thread by <see cref="LoudnessMeter.TakeSnapshot"/>.
/// </summary>
/// <param name="MomentaryLufs">Loudness over the last 400 ms (BS.1770 momentary window).</param>
/// <param name="ShortTermLufs">Loudness over the last 3 s (BS.1770 short-term window).</param>
/// <param name="IntegratedLufs">Gated program loudness since the last reset (absolute + relative gating).</param>
/// <param name="TruePeakDbtp">Peak-hold true peak since the last reset, in dBTP (may exceed 0).</param>
/// <param name="PeakDbLeft">Recent sample-peak of the left/mono channel, in dBFS.</param>
/// <param name="PeakDbRight">Recent sample-peak of the right channel, in dBFS (== left for mono).</param>
public readonly record struct LoudnessSnapshot(
    double MomentaryLufs,
    double ShortTermLufs,
    double IntegratedLufs,
    double TruePeakDbtp,
    double PeakDbLeft,
    double PeakDbRight)
{
    /// <summary>The all-silent read-out (every field <see cref="double.NegativeInfinity"/>).</summary>
    public static LoudnessSnapshot Silent { get; } = new(
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity,
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
}
