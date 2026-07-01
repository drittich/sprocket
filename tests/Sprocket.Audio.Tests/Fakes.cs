using Sprocket.Core.Audio;
using Sprocket.Core.Timing;

namespace Sprocket.Audio.Tests;

/// <summary>
/// A deterministic <see cref="IAudioOutput"/> for tests: <see cref="PlayedFrames"/> is set by the test (no
/// real device, no real time), enqueued buffers are captured for assertions, and <see cref="FreeFrames"/>
/// models a bounded queue so the engine's feeder fills then idles rather than spinning.
/// </summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly object _gate = new();
    private long _played;
    private long _totalEnqueued;       // sample-frames ever enqueued
    private readonly int _budgetFrames;

    public FakeAudioOutput(int budgetFrames = 8192) => _budgetFrames = budgetFrames;

    public int Channels { get; private set; } = 2;
    public int SampleRate { get; private set; } = 48000;
    public bool Playing { get; private set; }
    private readonly List<float[]> _enqueued = new();

    /// <summary>A thread-safe copy of every buffer enqueued so far (the feeder writes from its own thread).</summary>
    public float[][] EnqueuedSnapshot() { lock (_gate) return _enqueued.ToArray(); }

    public void Configure(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }

    public long PlayedFrames { get { lock (_gate) return _played; } }

    /// <summary>Test hook: pretend the device has played out to <paramref name="frames"/> total.</summary>
    public void SetPlayedFrames(long frames) { lock (_gate) _played = frames; }

    public int FreeFrames
    {
        get
        {
            lock (_gate)
            {
                long outstanding = _totalEnqueued - _played;
                long free = _budgetFrames - outstanding;
                return free <= 0 ? 0 : (int)free;
            }
        }
    }

    public void Enqueue(ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            _enqueued.Add(interleaved.ToArray());
            _totalEnqueued += interleaved.Length / Channels;
        }
    }

    public void Play() { lock (_gate) Playing = true; }
    public void Pause() { lock (_gate) Playing = false; }

    public void Flush()
    {
        lock (_gate) _totalEnqueued = _played; // drop queued-but-unplayed
    }

    public void Dispose() { }
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> that returns a constant value (or a 440 Hz-ish ramp) so the mixer can
/// be tested without FFmpeg. Records its <see cref="SeekTo"/> calls so seek-on-jump behaviour is observable.
/// </summary>
internal sealed class FakePcmReader : IPcmReader
{
    private readonly float _value;
    private long _frameCursor;

    public FakePcmReader(int sampleRate, int channels, float value)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _value = value;
    }

    public int Channels { get; }
    public int SampleRate { get; }
    public List<Timecode> Seeks { get; } = new();
    public bool Disposed { get; private set; }

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        destinationInterleaved.Fill(_value);
        _frameCursor += frames;
        return frames;
    }

    public void SeekTo(Timecode sourceTime)
    {
        Seeks.Add(sourceTime);
        _frameCursor = sourceTime.ToSampleIndex(SampleRate);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> whose sample value is a known linear ramp of the absolute source frame
/// index (× <see cref="_scale"/>, equal on every channel), so the retime resampler (PLAN.md step 21) can be
/// verified: an output sample produced from source position <c>p</c> reads back as <c>p × scale</c>. Records
/// seeks so streaming (seek-free) playback is observable.
/// </summary>
internal sealed class RampPcmReader : IPcmReader
{
    private readonly float _scale;
    private long _frame;

    public RampPcmReader(int sampleRate, int channels, float scale)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _scale = scale;
    }

    public int Channels { get; }
    public int SampleRate { get; }
    public List<Timecode> Seeks { get; } = new();

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        for (int f = 0; f < frames; f++)
        {
            float v = (_frame + f) * _scale;
            for (int ch = 0; ch < Channels; ch++)
                destinationInterleaved[f * Channels + ch] = v;
        }
        _frame += frames;
        return frames;
    }

    public void SeekTo(Timecode sourceTime)
    {
        Seeks.Add(sourceTime);
        _frame = sourceTime.ToSampleIndex(SampleRate);
    }

    public void Dispose() { }
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> that generates an endless sine of a given frequency and amplitude on every
/// channel — a realistic loudness test signal (unlike the DC-like <see cref="FakePcmReader"/>, which the
/// K-weighting high-pass would treat as silence). Used to exercise <c>LoudnessAnalyzer</c> without FFmpeg.
/// </summary>
internal sealed class SinePcmReader : IPcmReader
{
    private readonly double _freq;
    private readonly double _amp;
    private long _frame;

    public SinePcmReader(int sampleRate, int channels, double freq, double amp)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _freq = freq;
        _amp = amp;
    }

    public int Channels { get; }
    public int SampleRate { get; }

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        for (int f = 0; f < frames; f++)
        {
            double s = _amp * Math.Sin(2.0 * Math.PI * _freq * _frame / SampleRate);
            for (int ch = 0; ch < Channels; ch++)
                destinationInterleaved[f * Channels + ch] = (float)s;
            _frame++;
        }
        return frames;
    }

    public void SeekTo(Timecode sourceTime) => _frame = sourceTime.ToSampleIndex(SampleRate);

    public void Dispose() { }
}
