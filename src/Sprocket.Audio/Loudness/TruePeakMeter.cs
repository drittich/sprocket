namespace Sprocket.Audio.Loudness;

/// <summary>
/// Estimates the <b>true peak</b> (inter-sample peak, dBTP) of a signal by 4× oversampling with a polyphase
/// FIR interpolator, per ITU-R BS.1770-4 Annex 2. The sampled maximum can miss peaks that fall between two
/// samples; reconstructing intermediate points and taking the largest magnitude catches them, which matters for
/// delivery limits (a "-1 dBTP" ceiling) and for loudness-normalisation head-room.
/// </summary>
/// <remarks>
/// <para>The interpolation prototype is a Hann-windowed sinc built once at construction and split into 4
/// polyphase branches, each normalised to unity DC gain so a steady level reconstructs to itself. Per-channel
/// history rings and a running maximum make <see cref="Process"/> allocation-free; the running max is
/// peak-hold (only <see cref="Reset"/> lowers it). Not thread-safe: driven by the single audio feeder.</para>
/// </remarks>
internal sealed class TruePeakMeter
{
    private const int Oversample = 4;   // 4× is the BS.1770-4 minimum
    private const int Taps = 12;        // taps per polyphase branch

    private readonly double[][] _poly;   // [phase][tap] interpolation coefficients
    private readonly double[][] _history; // [channel][tap] most-recent-first ring
    private readonly int[] _cursor;       // per-channel index of the newest sample

    /// <summary>The largest reconstructed magnitude seen since the last <see cref="Reset"/> (peak-hold, linear).</summary>
    public double Max { get; private set; }

    public TruePeakMeter(int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _poly = BuildPolyphase();
        _history = new double[channels][];
        for (int c = 0; c < channels; c++)
            _history[c] = new double[Taps];
        _cursor = new int[channels];
    }

    /// <summary>Feeds one sample for <paramref name="channel"/>, updating the running true-peak maximum.</summary>
    public void Process(int channel, double sample)
    {
        double[] hist = _history[channel];
        int cur = _cursor[channel] = (_cursor[channel] + 1) % Taps;
        hist[cur] = sample;

        // The sample itself is a true-peak lower bound (phase 0 of an ideal interpolator).
        double abs = Math.Abs(sample);
        if (abs > Max) Max = abs;

        for (int p = 0; p < Oversample; p++)
        {
            double[] phase = _poly[p];
            double acc = 0.0;
            for (int j = 0; j < Taps; j++)
            {
                int idx = cur - j;
                if (idx < 0) idx += Taps;
                acc += phase[j] * hist[idx];
            }
            double a = Math.Abs(acc);
            if (a > Max) Max = a;
        }
    }

    /// <summary>Clears the running maximum and all per-channel history.</summary>
    public void Reset()
    {
        Max = 0.0;
        foreach (double[] h in _history)
            Array.Clear(h);
        Array.Clear(_cursor);
    }

    private static double[][] BuildPolyphase()
    {
        int n = Oversample * Taps;
        var proto = new double[n];
        double center = (n - 1) / 2.0;
        for (int i = 0; i < n; i++)
        {
            double t = (i - center) / Oversample;
            double sinc = t == 0.0 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            proto[i] = sinc * hann;
        }

        var poly = new double[Oversample][];
        for (int p = 0; p < Oversample; p++)
        {
            var branch = new double[Taps];
            double sum = 0.0;
            for (int j = 0; j < Taps; j++)
            {
                branch[j] = proto[p + Oversample * j];
                sum += branch[j];
            }
            // Normalise the branch to unity DC gain so a constant input reconstructs to itself.
            if (sum != 0.0)
                for (int j = 0; j < Taps; j++)
                    branch[j] /= sum;
            poly[p] = branch;
        }
        return poly;
    }
}
