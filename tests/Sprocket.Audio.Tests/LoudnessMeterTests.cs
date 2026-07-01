using Sprocket.Audio.Loudness;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic tests for the EBU R128 / ITU-R BS.1770-4 metering (PLAN.md step 30): the K-weighting filter
/// shape, true-peak inter-sample reconstruction, and the <see cref="LoudnessMeter"/>'s momentary/short-term
/// sliding windows, gated integrated loudness, and per-channel peaks. Signals are synthesised so the tests need
/// no audio device and hold to loudness invariants (silence, +6 dB per amplitude doubling, +3 dB stereo-vs-mono,
/// absolute/relative gating) rather than one hard-coded reference number.
/// </summary>
public class LoudnessMeterTests
{
    private const int Rate = 48000;

    // ---- signal helpers -----------------------------------------------------------------------------------

    /// <summary>Feeds a sine of the given amplitude into <paramref name="meter"/> in realistic small buffers.</summary>
    private static void FeedSine(
        LoudnessMeter meter, int channels, double freq, double amp, double seconds,
        bool leftOnly = false, double phaseRadians = 0.0)
    {
        int totalFrames = (int)(Rate * seconds);
        const int chunk = 1024;
        var buffer = new float[chunk * channels];
        long n = 0;
        int remaining = totalFrames;
        while (remaining > 0)
        {
            int frames = Math.Min(chunk, remaining);
            for (int f = 0; f < frames; f++)
            {
                double s = amp * Math.Sin(2.0 * Math.PI * freq * n / Rate + phaseRadians);
                for (int c = 0; c < channels; c++)
                    buffer[f * channels + c] = (float)(leftOnly && c > 0 ? 0.0 : s);
                n++;
            }
            meter.Process(buffer.AsSpan(0, frames * channels));
            remaining -= frames;
        }
    }

    private static void FeedSilence(LoudnessMeter meter, int channels, double seconds)
    {
        int totalFrames = (int)(Rate * seconds);
        const int chunk = 1024;
        var buffer = new float[chunk * channels]; // zeros
        int remaining = totalFrames;
        while (remaining > 0)
        {
            int frames = Math.Min(chunk, remaining);
            meter.Process(buffer.AsSpan(0, frames * channels));
            remaining -= frames;
        }
    }

    /// <summary>Feeds a constant (DC) value on every channel.</summary>
    private static void FeedDc(LoudnessMeter meter, int channels, double value, double seconds)
    {
        int totalFrames = (int)(Rate * seconds);
        const int chunk = 1024;
        var buffer = new float[chunk * channels];
        buffer.AsSpan().Fill((float)value);
        int remaining = totalFrames;
        while (remaining > 0)
        {
            int frames = Math.Min(chunk, remaining);
            meter.Process(buffer.AsSpan(0, frames * channels));
            remaining -= frames;
        }
    }

    private static double IntegratedOf(int channels, double freq, double amp, double seconds, bool leftOnly = false)
    {
        var meter = new LoudnessMeter(Rate, channels);
        FeedSine(meter, channels, freq, amp, seconds, leftOnly);
        meter.Flush();
        return meter.TakeSnapshot().IntegratedLufs;
    }

    // ---- K-weighting filter shape -------------------------------------------------------------------------

    [Fact]
    public void KWeighting_blocks_dc()
    {
        var filter = new KWeightingFilter(Rate, 1);
        double sumSq = 0;
        int n = Rate; // 1 s
        for (int i = 0; i < n; i++)
        {
            double y = filter.Process(0, 1.0); // constant DC input
            if (i >= n / 2) sumSq += y * y;     // measure after the filter settles
        }
        double rms = Math.Sqrt(sumSq / (n / 2));
        Assert.True(rms < 0.01, $"RLB high-pass should remove DC; residual RMS was {rms}");
    }

    [Theory]
    [InlineData(1000.0, -1.5, 1.5)]   // ~unity around 1 kHz
    [InlineData(10000.0, 2.5, 5.0)]   // +~4 dB high-shelf plateau
    [InlineData(30.0, -30.0, -3.0)]   // strong low-frequency roll-off
    public void KWeighting_gain_matches_curve(double freq, double minDb, double maxDb)
    {
        var filter = new KWeightingFilter(Rate, 1);
        int n = Rate; // 1 s so the response is steady
        double inSumSq = 0, outSumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Sin(2.0 * Math.PI * freq * i / Rate);
            double y = filter.Process(0, x);
            if (i >= n / 2) { inSumSq += x * x; outSumSq += y * y; }
        }
        double gainDb = 10.0 * Math.Log10(outSumSq / inSumSq);
        Assert.InRange(gainDb, minDb, maxDb);
    }

    // ---- true peak ----------------------------------------------------------------------------------------

    [Fact]
    public void TruePeak_catches_inter_sample_overshoot()
    {
        // A full-scale sine at fs/4, phased so every sample lands at ±0.707 — the true peak (~1.0) falls between
        // samples. The oversampling reconstruction must recover it well above the sampled peak.
        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, Rate / 4.0, 1.0, 0.5, phaseRadians: Math.PI / 4.0);
        LoudnessSnapshot s = meter.TakeSnapshot();

        Assert.True(s.PeakDbLeft < -2.5, $"sampled peak should be ~-3 dBFS, was {s.PeakDbLeft}");
        Assert.True(s.TruePeakDbtp > s.PeakDbLeft + 1.5, "true peak must exceed the sampled peak");
        Assert.True(s.TruePeakDbtp > -1.5, $"true peak should approach 0 dBTP, was {s.TruePeakDbtp}");
    }

    [Fact]
    public void TruePeak_at_least_sample_peak()
    {
        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, 1000.0, 0.5, 0.5);
        LoudnessSnapshot s = meter.TakeSnapshot();
        Assert.True(s.TruePeakDbtp >= s.PeakDbLeft - 0.01);
    }

    // ---- loudness invariants ------------------------------------------------------------------------------

    [Fact]
    public void Silence_reads_negative_infinity()
    {
        var meter = new LoudnessMeter(Rate, 2);
        FeedSilence(meter, 2, 2.0);
        meter.Flush();
        LoudnessSnapshot s = meter.TakeSnapshot();
        Assert.Equal(double.NegativeInfinity, s.MomentaryLufs);
        Assert.Equal(double.NegativeInfinity, s.ShortTermLufs);
        Assert.Equal(double.NegativeInfinity, s.IntegratedLufs);
    }

    [Fact]
    public void Dc_is_far_quieter_to_loudness_than_a_tone_but_shows_channel_peak()
    {
        // The RLB high-pass all but removes DC: a 0.5 DC offset reads dramatically quieter than a 0.5 tone (an
        // onset transient keeps it from being exactly -∞), while the sample-peak meter still shows ~-6 dBFS.
        double tone = IntegratedOf(2, 1000.0, 0.5, 2.0);

        var meter = new LoudnessMeter(Rate, 2);
        FeedDc(meter, 2, 0.5, 2.0);
        meter.Flush();
        LoudnessSnapshot s = meter.TakeSnapshot();

        Assert.True(s.IntegratedLufs < tone - 15.0, $"DC should read far quieter than the tone; was {s.IntegratedLufs} vs {tone}");
        Assert.InRange(s.PeakDbLeft, -6.5, -5.5); // 0.5 → ~-6 dBFS, unaffected by K-weighting
    }

    [Fact]
    public void Doubling_amplitude_raises_loudness_by_six_db()
    {
        double quiet = IntegratedOf(2, 1000.0, 0.25, 3.0);
        double loud = IntegratedOf(2, 1000.0, 0.5, 3.0);
        Assert.Equal(6.02, loud - quiet, precision: 1); // 20·log10(2) ≈ 6.02 LU
    }

    [Fact]
    public void Stereo_is_three_db_louder_than_the_same_signal_on_one_channel()
    {
        double mono = IntegratedOf(2, 1000.0, 0.5, 3.0, leftOnly: true);
        double stereo = IntegratedOf(2, 1000.0, 0.5, 3.0);
        Assert.Equal(3.01, stereo - mono, precision: 1); // 10·log10(2) ≈ 3.01 LU
    }

    [Fact]
    public void FullScale_1khz_stereo_sine_calibrates_near_minus_one_lufs()
    {
        double integrated = IntegratedOf(2, 1000.0, 1.0, 3.0);
        // -0.691 offset + ~0 dB K-weighting at 1 kHz for a full-scale stereo sine ⇒ roughly -0.7 LUFS.
        Assert.InRange(integrated, -2.5, 1.0);
    }

    [Fact]
    public void Absolute_gate_ignores_a_near_silent_tail()
    {
        double loudOnly = IntegratedOf(2, 1000.0, 0.5, 3.0);

        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, 1000.0, 0.5, 3.0);
        FeedSine(meter, 2, 1000.0, 0.5 * Math.Pow(10, -80 / 20.0), 3.0); // ~-80 dB below → far under the -70 gate
        meter.Flush();
        double withTail = meter.TakeSnapshot().IntegratedLufs;

        // The near-silent tail is gated out — integrated stays at the loud level (within a boundary-block sliver),
        // nowhere near the ~-3 dB drop that including the silent half would cause.
        Assert.InRange(withTail, loudOnly - 0.5, loudOnly + 0.5);
    }

    [Fact]
    public void Relative_gate_ignores_a_much_quieter_section()
    {
        double loudOnly = IntegratedOf(2, 1000.0, 0.5, 4.0);

        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, 1000.0, 0.5, 4.0);
        FeedSine(meter, 2, 1000.0, 0.5 * Math.Pow(10, -15 / 20.0), 1.0); // 15 LU quieter → below the relative gate
        meter.Flush();
        double withQuiet = meter.TakeSnapshot().IntegratedLufs;

        // The quieter section is >10 LU below the mean, so the relative gate drops it — integrated stays at the
        // loud level rather than the ~-0.9 dB dip that including it would cause.
        Assert.InRange(withQuiet, loudOnly - 0.5, loudOnly + 0.5);
    }

    [Fact]
    public void Momentary_window_is_shorter_than_short_term()
    {
        // Tone then silence: after >400 ms of silence the momentary (400 ms) window is empty of signal while the
        // short-term (3 s) window still contains the tone.
        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, 1000.0, 0.5, 1.0);
        FeedSilence(meter, 2, 0.6);
        LoudnessSnapshot s = meter.TakeSnapshot();

        Assert.True(s.MomentaryLufs < -70.0, $"momentary should have decayed to silence, was {s.MomentaryLufs}");
        Assert.True(s.ShortTermLufs > -30.0, $"short-term should still see the recent tone, was {s.ShortTermLufs}");
    }

    [Fact]
    public void Reset_restarts_the_integrated_measurement()
    {
        var meter = new LoudnessMeter(Rate, 2);
        FeedSine(meter, 2, 1000.0, 0.5, 2.0);
        Assert.True(meter.TakeSnapshot().IntegratedLufs > -70.0);

        meter.RequestReset();
        FeedSilence(meter, 2, 0.2); // the next Process honours the reset request
        LoudnessSnapshot s = meter.TakeSnapshot();
        Assert.Equal(double.NegativeInfinity, s.IntegratedLufs);
    }
}
