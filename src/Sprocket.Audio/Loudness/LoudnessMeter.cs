namespace Sprocket.Audio.Loudness;

/// <summary>
/// A real-time loudness meter implementing EBU R128 / ITU-R BS.1770-4 over interleaved float32 audio: momentary
/// (400 ms) and short-term (3 s) sliding-window loudness, gated integrated program loudness, true peak (dBTP),
/// and per-channel sample peak — all in LUFS/dBTP/dBFS (see <see cref="LoudnessSnapshot"/>). It is fed the same
/// mixed buffers the device plays, so the read-out reflects exactly what is heard.
/// </summary>
/// <remarks>
/// <para><b>Allocation-free on the audio path.</b> <see cref="Process"/> only mutates preallocated per-channel
/// filter state, fixed-size segment rings, and a bounded loudness histogram — nothing on the managed heap per
/// buffer (ARCHITECTURE.md §1/§6). Energy is accumulated in 100 ms segments; each completed segment updates the
/// sliding windows and (once four segments span a 400 ms gating block) the integrated histogram.</para>
/// <para><b>Threading.</b> <see cref="Process"/>, <see cref="Flush"/> and reset run on the single audio-feeder
/// thread; the read-out doubles are published under a small lock at each 100 ms segment boundary and read by
/// <see cref="TakeSnapshot"/> from the UI thread. <see cref="RequestReset"/> is thread-safe (a flag the feeder
/// honours at the next <see cref="Process"/>), so a transport seek can restart the integrated measurement
/// without touching feeder-confined state from the UI thread.</para>
/// </remarks>
public sealed class LoudnessMeter
{
    private const double Offset = -0.691;          // BS.1770 loudness offset (LKFS)
    private const double AbsoluteGateLufs = -70.0; // absolute gate for integrated loudness

    private const int MomentarySegments = 4;       // 4 × 100 ms = 400 ms
    private const int ShortTermSegments = 30;      // 30 × 100 ms = 3 s

    // Integrated-loudness histogram: 0.1 LU bins from the absolute gate up to a generous ceiling.
    private const double HistMinLufs = AbsoluteGateLufs;
    private const double HistStepLu = 0.1;
    private const int HistBins = 751;              // -70.0 .. +5.0 LUFS inclusive

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _segmentFrames;           // frames per 100 ms segment
    private readonly KWeightingFilter _filter;
    private readonly TruePeakMeter _truePeak;

    // Current (in-progress) segment accumulators — feeder-thread-confined.
    private int _segFrames;
    private double _segSumSq;                       // Σ frames of Σ_ch G·y²
    private readonly double[] _segChannelPeak;      // max |x| per channel this segment

    // Ring of the last ShortTermSegments completed segments (sumSq + frame count) — feeder-thread-confined.
    private readonly double[] _ringSumSq = new double[ShortTermSegments];
    private readonly int[] _ringFrames = new int[ShortTermSegments];
    private int _ringHead = -1;                     // index of the most-recently-completed segment
    private int _ringCount;

    private readonly long[] _hist = new long[HistBins]; // integrated-loudness block histogram

    private volatile bool _resetRequested;

    // Published read-out (updated at each segment boundary under the lock; read by TakeSnapshot).
    private readonly object _publishGate = new();
    private double _pubMomentary = double.NegativeInfinity;
    private double _pubShortTerm = double.NegativeInfinity;
    private double _pubIntegrated = double.NegativeInfinity;
    private double _pubTruePeak = double.NegativeInfinity;
    private double _pubPeakL = double.NegativeInfinity;
    private double _pubPeakR = double.NegativeInfinity;

    /// <summary>Creates a meter for the given output format. Only mono and stereo are metered (the device path);
    /// extra channels are ignored.</summary>
    public LoudnessMeter(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _sampleRate = sampleRate;
        _channels = channels;
        _segmentFrames = Math.Max(1, sampleRate / 10);
        _filter = new KWeightingFilter(sampleRate, channels);
        _truePeak = new TruePeakMeter(channels);
        _segChannelPeak = new double[channels];
    }

    /// <summary>
    /// Feeds one interleaved float32 buffer (channel-count must match the meter's) into the measurement. Safe to
    /// call only from the audio-feeder thread.
    /// </summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        if (_resetRequested)
            ResetState();

        int c = _channels;
        int frames = interleaved.Length / c;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * c;
            double frameSumSq = 0.0;
            for (int ch = 0; ch < c; ch++)
            {
                double x = interleaved[baseIndex + ch];
                double ax = Math.Abs(x);
                if (ax > _segChannelPeak[ch]) _segChannelPeak[ch] = ax;
                _truePeak.Process(ch, x);

                double y = _filter.Process(ch, x);
                frameSumSq += y * y; // channel weight G = 1.0 for mono/stereo (BS.1770)
            }
            _segSumSq += frameSumSq;
            if (++_segFrames >= _segmentFrames)
                CompleteSegment();
        }
    }

    /// <summary>Completes any in-progress partial segment so a finite offline stream's tail is measured. Real-time
    /// callers need not call this (the sliding windows self-update at each segment boundary).</summary>
    public void Flush()
    {
        if (_segFrames > 0)
            CompleteSegment();
    }

    /// <summary>Requests that the integrated measurement (and all windows/filters) restart at the next
    /// <see cref="Process"/>. Thread-safe.</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>Reads the current published loudness values. Safe to call from any thread.</summary>
    public LoudnessSnapshot TakeSnapshot()
    {
        lock (_publishGate)
            return new LoudnessSnapshot(
                _pubMomentary, _pubShortTerm, _pubIntegrated, _pubTruePeak, _pubPeakL, _pubPeakR);
    }

    private void CompleteSegment()
    {
        // Push the finished segment into the ring.
        _ringHead = (_ringHead + 1) % ShortTermSegments;
        _ringSumSq[_ringHead] = _segSumSq;
        _ringFrames[_ringHead] = _segFrames;
        if (_ringCount < ShortTermSegments) _ringCount++;

        double momentary = WindowLoudness(MomentarySegments);
        double shortTerm = WindowLoudness(ShortTermSegments);

        // A 400 ms gating block = the last four segments (100 ms hop → 75% overlap). Gate absolutely at -70 LUFS.
        if (_ringCount >= MomentarySegments)
        {
            double blockMeanSq = WindowMeanSq(MomentarySegments);
            if (blockMeanSq > 0.0)
            {
                double blockLoudness = Offset + 10.0 * Math.Log10(blockMeanSq);
                if (blockLoudness >= AbsoluteGateLufs)
                    _hist[BinOf(blockLoudness)]++;
            }
        }

        double integrated = IntegratedFromHistogram();
        double truePeakDb = ToDb(_truePeak.Max);
        double peakL = ToDb(_segChannelPeak[0]);
        double peakR = _channels > 1 ? ToDb(_segChannelPeak[1]) : peakL;

        lock (_publishGate)
        {
            _pubMomentary = momentary;
            _pubShortTerm = shortTerm;
            _pubIntegrated = integrated;
            _pubTruePeak = truePeakDb;
            _pubPeakL = peakL;
            _pubPeakR = peakR;
        }

        // Reset the segment accumulators for the next segment.
        _segSumSq = 0.0;
        _segFrames = 0;
        Array.Clear(_segChannelPeak);
    }

    /// <summary>Mean square over the last <paramref name="n"/> completed segments (0 if none).</summary>
    private double WindowMeanSq(int n)
    {
        int take = Math.Min(n, _ringCount);
        if (take == 0) return 0.0;
        double sumSq = 0.0;
        long frames = 0;
        for (int i = 0; i < take; i++)
        {
            int idx = _ringHead - i;
            if (idx < 0) idx += ShortTermSegments;
            sumSq += _ringSumSq[idx];
            frames += _ringFrames[idx];
        }
        return frames == 0 ? 0.0 : sumSq / frames;
    }

    private double WindowLoudness(int n)
    {
        double meanSq = WindowMeanSq(n);
        return meanSq > 0.0 ? Offset + 10.0 * Math.Log10(meanSq) : double.NegativeInfinity;
    }

    /// <summary>Gated integrated loudness (BS.1770): mean energy of the absolutely-gated blocks, then re-meaned
    /// over blocks at or above the relative gate (their mean − 10 LU).</summary>
    private double IntegratedFromHistogram()
    {
        double sumZ = 0.0;
        long n = 0;
        for (int b = 0; b < HistBins; b++)
        {
            long count = _hist[b];
            if (count == 0) continue;
            sumZ += count * EnergyOfBin(b);
            n += count;
        }
        if (n == 0) return double.NegativeInfinity;

        double relativeGate = Offset + 10.0 * Math.Log10(sumZ / n) - 10.0;

        double sumZ2 = 0.0;
        long n2 = 0;
        for (int b = 0; b < HistBins; b++)
        {
            long count = _hist[b];
            if (count == 0) continue;
            if (LoudnessOfBin(b) < relativeGate) continue;
            sumZ2 += count * EnergyOfBin(b);
            n2 += count;
        }
        return n2 == 0 ? double.NegativeInfinity : Offset + 10.0 * Math.Log10(sumZ2 / n2);
    }

    private static int BinOf(double loudness)
    {
        int bin = (int)Math.Round((loudness - HistMinLufs) / HistStepLu);
        return Math.Clamp(bin, 0, HistBins - 1);
    }

    private static double LoudnessOfBin(int bin) => HistMinLufs + bin * HistStepLu;

    private static double EnergyOfBin(int bin) => Math.Pow(10.0, (LoudnessOfBin(bin) - Offset) / 10.0);

    private static double ToDb(double linear) => linear > 0.0 ? 20.0 * Math.Log10(linear) : double.NegativeInfinity;

    private void ResetState()
    {
        _resetRequested = false;
        _filter.Reset();
        _truePeak.Reset();
        _segSumSq = 0.0;
        _segFrames = 0;
        Array.Clear(_segChannelPeak);
        Array.Clear(_ringSumSq);
        Array.Clear(_ringFrames);
        _ringHead = -1;
        _ringCount = 0;
        Array.Clear(_hist);

        lock (_publishGate)
        {
            _pubMomentary = _pubShortTerm = _pubIntegrated = double.NegativeInfinity;
            _pubTruePeak = _pubPeakL = _pubPeakR = double.NegativeInfinity;
        }
    }
}
