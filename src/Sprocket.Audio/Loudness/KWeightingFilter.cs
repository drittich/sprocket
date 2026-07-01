namespace Sprocket.Audio.Loudness;

/// <summary>
/// The two-stage "K-weighting" pre-filter of ITU-R BS.1770-4 / EBU R128: a high-shelf ("head model") biquad
/// followed by a second-order RLB high-pass, cascaded per channel. Loudness (LUFS) is the mean square of the
/// K-weighted signal (see <see cref="LoudnessMeter"/>).
/// </summary>
/// <remarks>
/// <para>The analog prototypes and their bilinear-transform mapping are the ones libebur128 uses, so the
/// coefficients are computed for <em>any</em> sample rate (not just the 48 kHz table printed in the standard)
/// and match the reference implementation at 48 kHz. Each biquad runs as a Transposed-Direct-Form-II section
/// (a0 normalised to 1), which is numerically well behaved for these near-DC / near-Nyquist poles.</para>
/// <para>Per-channel filter state (<c>z1</c>/<c>z2</c> for each of the two sections) is held in flat arrays
/// indexed by channel, so filtering a sample is allocation-free. Not thread-safe: one instance is driven by a
/// single thread (the audio feeder).</para>
/// </remarks>
internal sealed class KWeightingFilter
{
    // Stage 1 — pre-filter (high-shelf, "head model") coefficients (a0 == 1).
    private readonly double _pb0, _pb1, _pb2, _pa1, _pa2;
    // Stage 2 — RLB high-pass coefficients (a0 == 1); its numerator is the fixed { 1, -2, 1 }.
    private readonly double _ra1, _ra2;

    private readonly double[] _preZ1, _preZ2;   // stage-1 state per channel
    private readonly double[] _rlbZ1, _rlbZ2;   // stage-2 state per channel

    public KWeightingFilter(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        // Pre-filter: high-shelf with fc = 1681.97 Hz, +3.9998 dB, Q = 0.7071752 (BS.1770 head model).
        {
            const double f0 = 1681.974450955533;
            const double gainDb = 3.999843853973347;
            const double q = 0.7071752369554196;

            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10.0, gainDb / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;

            _pb0 = (vh + vb * k / q + k * k) / a0;
            _pb1 = 2.0 * (k * k - vh) / a0;
            _pb2 = (vh - vb * k / q + k * k) / a0;
            _pa1 = 2.0 * (k * k - 1.0) / a0;
            _pa2 = (1.0 - k / q + k * k) / a0;
        }

        // RLB high-pass: fc = 38.135 Hz, Q = 0.5003270 (numerator fixed at { 1, -2, 1 }).
        {
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;

            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1.0 + k / q + k * k;
            _ra1 = 2.0 * (k * k - 1.0) / a0;
            _ra2 = (1.0 - k / q + k * k) / a0;
        }

        _preZ1 = new double[channels];
        _preZ2 = new double[channels];
        _rlbZ1 = new double[channels];
        _rlbZ2 = new double[channels];
    }

    /// <summary>Filters one sample for <paramref name="channel"/> and returns the K-weighted output.</summary>
    public double Process(int channel, double x)
    {
        // Stage 1 (TDF-II): high-shelf.
        double y1 = _pb0 * x + _preZ1[channel];
        _preZ1[channel] = _pb1 * x - _pa1 * y1 + _preZ2[channel];
        _preZ2[channel] = _pb2 * x - _pa2 * y1;

        // Stage 2 (TDF-II): RLB high-pass, numerator { 1, -2, 1 }.
        double y2 = y1 + _rlbZ1[channel];
        _rlbZ1[channel] = -2.0 * y1 - _ra1 * y2 + _rlbZ2[channel];
        _rlbZ2[channel] = y1 - _ra2 * y2;
        return y2;
    }

    /// <summary>Clears all per-channel filter memory (e.g. after a transport seek).</summary>
    public void Reset()
    {
        Array.Clear(_preZ1);
        Array.Clear(_preZ2);
        Array.Clear(_rlbZ1);
        Array.Clear(_rlbZ2);
    }
}
