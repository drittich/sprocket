using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;

namespace Sprocket.Audio.Loudness;

/// <summary>The result of an offline loudness measurement: gated integrated loudness (LUFS) and true peak (dBTP).</summary>
public readonly record struct LoudnessMeasurement(double IntegratedLufs, double TruePeakDbtp)
{
    /// <summary>A measurement of silence (both fields <see cref="double.NegativeInfinity"/>).</summary>
    public static LoudnessMeasurement Silent { get; } = new(double.NegativeInfinity, double.NegativeInfinity);
}

/// <summary>
/// Measures the EBU R128 / BS.1770 loudness of audio <em>offline</em> (faster than real time) by pulling it
/// through a <see cref="LoudnessMeter"/>, for loudness normalization (PLAN.md step 30). A single source over its
/// used span (clip normalization) or a mixed sequence with a measurement scope (track / master normalization).
/// </summary>
/// <remarks>Each measurement uses its own meter and a single reusable buffer, so it does not touch the live
/// playback meter and allocates only that one buffer (never per chunk).</remarks>
public static class LoudnessAnalyzer
{
    private const int DefaultBufferFrames = 4096;

    /// <summary>
    /// Measures a single PCM source over <c>[<paramref name="start"/>, start + <paramref name="duration"/>)</c> at
    /// unity gain — the clip's <em>raw</em> loudness, used to normalize a clip by setting its
    /// <see cref="Clip.GainDb"/>. Positions the reader at <paramref name="start"/> first.
    /// </summary>
    public static LoudnessMeasurement MeasureSource(
        IPcmReader reader, Timecode start, Timecode duration, int bufferFrames = DefaultBufferFrames)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (bufferFrames <= 0) throw new ArgumentOutOfRangeException(nameof(bufferFrames));
        if (duration.Ticks <= 0) return LoudnessMeasurement.Silent;

        var meter = new LoudnessMeter(reader.SampleRate, reader.Channels);
        var buffer = new float[bufferFrames * reader.Channels];

        reader.SeekTo(start);
        long total = duration.ToSampleIndex(reader.SampleRate);
        long done = 0;
        while (done < total)
        {
            int want = (int)Math.Min(bufferFrames, total - done);
            int got = reader.Read(buffer.AsSpan(0, want * reader.Channels));
            if (got <= 0) break; // end of stream
            meter.Process(buffer.AsSpan(0, got * reader.Channels));
            done += got;
            if (got < want) break; // a short read is EOF
        }

        return Result(meter);
    }

    /// <summary>
    /// Measures a mixed sequence over <c>[<paramref name="start"/>, start + <paramref name="duration"/>)</c>. With
    /// no <paramref name="scope"/> this is the full mix (master normalization); a scope isolating a track (unity
    /// track/master gain) measures that track's raw contribution (track normalization). The <paramref name="mixer"/>
    /// must already be able to resolve the sequence's sources.
    /// </summary>
    public static LoudnessMeasurement MeasureMix(
        AudioMixer mixer, Project project, Sequence sequence, Timecode start, Timecode duration,
        AudioPlanScope? scope = null, int bufferFrames = DefaultBufferFrames)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (bufferFrames <= 0) throw new ArgumentOutOfRangeException(nameof(bufferFrames));
        if (duration.Ticks <= 0) return LoudnessMeasurement.Silent;

        var meter = new LoudnessMeter(mixer.SampleRate, mixer.Channels);
        var buffer = new float[bufferFrames * mixer.Channels];

        Timecode pos = start;
        Timecode end = start + duration;
        while (pos < end)
        {
            long remaining = (end - pos).ToSampleIndex(mixer.SampleRate);
            int want = (int)Math.Min(bufferFrames, remaining);
            if (want <= 0) break;
            mixer.MixInto(buffer.AsSpan(0, want * mixer.Channels), pos, project, sequence, scope);
            meter.Process(buffer.AsSpan(0, want * mixer.Channels));
            pos += Timecode.FromSamples(want, mixer.SampleRate);
        }

        return Result(meter);
    }

    private static LoudnessMeasurement Result(LoudnessMeter meter)
    {
        meter.Flush();
        LoudnessSnapshot s = meter.TakeSnapshot();
        return new LoudnessMeasurement(s.IntegratedLufs, s.TruePeakDbtp);
    }
}
