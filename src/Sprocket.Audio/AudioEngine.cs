using Sprocket.Audio.Loudness;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Audio;

/// <summary>
/// The audio master clock (ARCHITECTURE.md §8): a transport-capable <see cref="IMasterClock"/> whose
/// <see cref="Now"/> is derived from the count of sample-frames the device has actually <em>played</em>, so
/// audio is the heartbeat and video follows it. A background feeder keeps the device queue topped up by
/// mixing the timeline through an <see cref="AudioMixer"/> for the advancing write cursor.
/// </summary>
/// <remarks>
/// <para>This is what <c>PlaybackEngine</c> receives as its clock when the project has audio; the engine's
/// pump reads <see cref="Now"/> and issues <see cref="Start"/>/<see cref="Pause"/>/<see cref="Seek"/> exactly
/// as it would to the software clock. Seeks bump a generation so an in-flight mix for a superseded position is
/// dropped rather than enqueued (the same discipline the video decode ring uses).</para>
/// <para><see cref="Now"/>/<see cref="IsRunning"/> are safe to read from any thread; the transport methods are
/// called from the UI thread; the feeder owns all mixing. The engine takes ownership of the output and mixer
/// and disposes them.</para>
/// </remarks>
public sealed class AudioEngine : IMasterClock, IAsyncDisposable
{
    /// <summary>Frames per mixed/enqueued buffer (~43 ms at 48 kHz) — small enough for responsive sync, large
    /// enough to keep mixing overhead low.</summary>
    public const int DefaultBufferFrames = 2048;

    private readonly IAudioOutput _output;
    private readonly AudioMixer _mixer;
    private readonly Project _project;
    private readonly int _sampleRate;
    private readonly int _bufferFrames;
    private readonly float[] _mixBuffer;
    private readonly LoudnessMeter _meter;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _feeder;

    private bool _running;
    private Timecode _anchorTimeline;     // timeline position captured at the last (re)anchor
    private long _anchorPlayedFrames;     // _output.PlayedFrames captured at that anchor
    private Timecode _pausedAt;           // position to report while paused
    private Timecode _writeCursor;        // next timeline position the feeder will mix from
    private long _generation;             // bumped by Seek; a mix tagged with a stale generation is dropped
    private bool _disposed;

    /// <summary>Creates the engine over an already-<see cref="IAudioOutput.Configure">configured</see> output and
    /// a mixer built for the same format. Starts the (idle-until-playing) feeder.</summary>
    public AudioEngine(IAudioOutput output, AudioMixer mixer, Project project, int? bufferFrames = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(project);

        _output = output;
        _mixer = mixer;
        _project = project;
        _sampleRate = output.SampleRate;
        _bufferFrames = bufferFrames ?? DefaultBufferFrames;
        _mixBuffer = new float[_bufferFrames * output.Channels];
        _meter = new LoudnessMeter(output.SampleRate, output.Channels);
        _feeder = Task.Run(() => FeedLoopAsync(_stop.Token));
    }

    /// <summary>
    /// The current EBU R128 / BS.1770 loudness read-out of the mixed program (what the device plays). Safe to read
    /// from any thread; updates ~10× per second while playing and freezes when paused (PLAN.md step 30). The
    /// integrated measurement restarts on <see cref="Seek"/>.
    /// </summary>
    public LoudnessSnapshot CurrentLoudness => _meter.TakeSnapshot();

    /// <inheritdoc />
    public Timecode Now
    {
        get { lock (_gate) return NowLocked(); }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_running)
                return;
            _anchorTimeline = _pausedAt;
            _anchorPlayedFrames = _output.PlayedFrames;
            _output.Play();
            _running = true;
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!_running)
                return;
            _pausedAt = NowLocked();
            _output.Pause();
            _running = false;
        }
    }

    /// <inheritdoc />
    public void Seek(Timecode position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _output.Flush();
            _writeCursor = position;
            _anchorTimeline = position;
            _anchorPlayedFrames = _output.PlayedFrames;
            _pausedAt = position;
            _generation++;
            _meter.RequestReset(); // restart the integrated measurement from the new position
        }
    }

    private Timecode NowLocked()
    {
        if (!_running)
            return _pausedAt;
        long played = _output.PlayedFrames - _anchorPlayedFrames;
        if (played < 0)
            played = 0;
        return _anchorTimeline + Timecode.FromSamples(played, _sampleRate);
    }

    private async Task FeedLoopAsync(CancellationToken ct)
    {
        Timecode advance = Timecode.FromSamples(_bufferFrames, _sampleRate);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Timecode pos;
                long gen;
                lock (_gate)
                {
                    if (!_running || _output.FreeFrames < _bufferFrames)
                    {
                        // Paused or the device queue is full — nothing to do this tick.
                        pos = default;
                        gen = -1;
                    }
                    else
                    {
                        pos = _writeCursor;
                        gen = _generation;
                    }
                }

                if (gen < 0)
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                    continue;
                }

                _mixer.MixInto(_mixBuffer, pos, _project); // mixing/decoding happens off the lock

                bool enqueued;
                lock (_gate)
                {
                    enqueued = gen == _generation && _running;
                    if (enqueued)
                    {
                        _output.Enqueue(_mixBuffer);
                        _writeCursor = pos + advance;
                    }
                    // else: a seek superseded this buffer — drop it; the next tick mixes the new position.
                }

                // Meter only what was actually queued for playback, and off the lock (the K-weighting/true-peak
                // DSP must not stall Now/transport). RequestReset (from Seek) is honoured inside Process.
                if (enqueued)
                    _meter.Process(_mixBuffer);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stop.Cancel();
        try { await _feeder.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _mixer.Dispose();
        _output.Dispose();
        _stop.Dispose();
    }
}
