using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// The result of audio-waveform sync analysis (PLAN.md step 24): the lag that best aligns a candidate angle's
/// audio with the reference, and a confidence in <c>[-1, 1]</c> (the normalized cross-correlation peak — near 1
/// is a strong match, near 0 means no clear alignment was found).
/// </summary>
/// <param name="LagSamples">How many samples the candidate is delayed relative to the reference (its content
/// occurs this much later in its own stream). May be negative. The angle's <see cref="MulticamAngle.SyncOffset"/>
/// is this lag in time.</param>
/// <param name="Confidence">Normalized cross-correlation at the best lag, in <c>[-1, 1]</c>.</param>
public readonly record struct AudioSyncResult(int LagSamples, double Confidence);

/// <summary>
/// Pure audio-waveform cross-correlation for multicam/clip sync (PLAN.md step 24). Given two mono PCM signals
/// (the reference angle and a candidate angle, already read at a common sample rate — the App pulls them through
/// <c>Sprocket.Media.AudioSource</c>, reusing the step-15 PCM path), it finds the integer sample lag that
/// maximizes their normalized cross-correlation. No DSP dependency, no allocation per sample beyond the result,
/// and fully deterministic, so it is unit-tested headlessly like the other sync reasoning.
/// </summary>
public static class AudioSync
{
    /// <summary>
    /// Finds the lag (in samples) of <paramref name="candidate"/> relative to <paramref name="reference"/> that
    /// best aligns them, searched over <c>[-<paramref name="maxLagSamples"/>, +maxLagSamples]</c>. A positive lag
    /// <c>k</c> means the candidate's content occurs <c>k</c> samples later than the reference's (i.e.
    /// <c>candidate[n] ≈ reference[n − k]</c>); that lag is the candidate angle's sync offset.
    /// </summary>
    /// <remarks>
    /// Each candidate lag is scored by the energy-normalized cross-correlation over the overlapping region, so the
    /// score is comparable across lags (it does not simply favour the lag with the most overlap). Lags whose
    /// overlap is shorter than <paramref name="minOverlapSamples"/> (default: a quarter of the shorter signal) are
    /// skipped, so a near-edge spurious match can't win. Returns lag 0 / confidence 0 when neither signal carries
    /// energy or no lag clears the overlap floor.
    /// </remarks>
    public static AudioSyncResult FindBestLag(
        ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int maxLagSamples, int minOverlapSamples = -1)
    {
        if (maxLagSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLagSamples), "Maximum lag must be non-negative.");
        if (reference.Length == 0 || candidate.Length == 0)
            return new AudioSyncResult(0, 0);

        if (minOverlapSamples < 0)
            minOverlapSamples = Math.Max(1, Math.Min(reference.Length, candidate.Length) / 4);

        int bestLag = 0;
        double bestScore = double.NegativeInfinity;
        bool any = false;

        for (int d = -maxLagSamples; d <= maxLagSamples; d++)
        {
            // Overlap of reference[n] with candidate[n + d]: n must be valid in both spans.
            int nStart = Math.Max(0, -d);
            int nEnd = Math.Min(reference.Length, candidate.Length - d);
            if (nEnd - nStart < minOverlapSamples)
                continue;

            double dot = 0, energyRef = 0, energyCand = 0;
            for (int n = nStart; n < nEnd; n++)
            {
                float r = reference[n];
                float c = candidate[n + d];
                dot += (double)r * c;
                energyRef += (double)r * r;
                energyCand += (double)c * c;
            }

            double denom = Math.Sqrt(energyRef * energyCand);
            double score = denom > 0 ? dot / denom : 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = d;
                any = true;
            }
        }

        return any ? new AudioSyncResult(bestLag, bestScore) : new AudioSyncResult(0, 0);
    }

    /// <summary>
    /// The candidate angle's sync offset as a <see cref="Timecode"/> — <see cref="FindBestLag"/> converted from
    /// samples at <paramref name="sampleRate"/>. The returned offset is fed straight into
    /// <see cref="MulticamAngle.SyncOffset"/>.
    /// </summary>
    public static (Timecode Offset, double Confidence) FindBestOffset(
        ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int sampleRate, int maxLagSamples,
        int minOverlapSamples = -1)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        AudioSyncResult result = FindBestLag(reference, candidate, maxLagSamples, minOverlapSamples);
        return (Timecode.FromSamples(result.LagSamples, sampleRate), result.Confidence);
    }
}
