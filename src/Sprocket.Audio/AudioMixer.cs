using System.Numerics;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;

namespace Sprocket.Audio;

/// <summary>
/// Fills one interleaved float32 output buffer for a timeline range by executing the render graph's
/// <see cref="AudioBufferPlan"/> (ARCHITECTURE.md §6): for each audible audio layer it pulls PCM from that
/// source through the <see cref="IPcmReader"/> seam, applies the layer's gain envelope (a linear ramp across
/// the buffer, which is how fades work), and sums into the mix; then it applies master gain and clamps.
/// </summary>
/// <remarks>
/// <para>The mixer keeps each source's reader positioned for sequential playback and only issues a
/// <see cref="IPcmReader.SeekTo"/> when the requested source time jumps (a scrub), so steady playback never
/// re-seeks. It is driven by a single feeder thread (the audio engine), so it holds no locks.</para>
/// <para>Readers are resolved lazily by <see cref="MediaRefId"/> and owned by the mixer (disposed with it).</para>
/// </remarks>
public sealed class AudioMixer : IDisposable
{
    // Re-seek a reader only when the requested source time drifts beyond this; sequential playback stays within
    // sub-sample rounding, so this avoids needless seeks while still catching real scrubs.
    private static readonly long SeekToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private sealed class SourceState
    {
        public required IPcmReader Reader;
        public Timecode NextSourceTime;
        public bool Positioned;

        // Retime resampler state (PLAN.md step 21), used only by retimed (speed ≠ 1) layers. A streaming linear
        // resampler: Window holds source frames already pulled but not yet fully consumed (carried across buffers
        // so reading stays sequential — no per-buffer seek), and Phase is the fractional position of the next
        // output sample measured from Window[0]. Reset whenever the reader is (re)seeked.
        public float[] Window = [];
        public int WindowFrames;
        public double Phase;

        public void ResetResampler()
        {
            WindowFrames = 0;
            Phase = 0;
        }
    }

    private readonly Func<MediaRefId, IPcmReader?> _resolve;
    private readonly Dictionary<MediaRefId, SourceState> _states = new();
    // One layer scratch buffer per nesting depth (PLAN.md step 23): mixing a nested-sequence layer recurses, and
    // each depth needs its own scratch so the sub-mix doesn't clobber the parent's. Index 0 is the (common,
    // non-nested) top level — kept allocation-free in steady state exactly as before.
    private readonly List<float[]> _scratchByDepth = new();
    private bool _disposed;

    /// <summary>Creates a mixer for the given output format. <paramref name="resolveReader"/> maps a media id to
    /// its PCM reader (returning null for an unavailable/offline source, which mixes as silence).</summary>
    public AudioMixer(int sampleRate, int channels, Func<MediaRefId, IPcmReader?> resolveReader)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        ArgumentNullException.ThrowIfNull(resolveReader);
        SampleRate = sampleRate;
        Channels = channels;
        _resolve = resolveReader;
    }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Output channel count.</summary>
    public int Channels { get; }

    /// <summary>
    /// Mixes the timeline audio for the buffer that starts at <paramref name="timelineStart"/> and spans
    /// <c><paramref name="destinationInterleaved"/>.Length / Channels</c> sample-frames into the destination
    /// (fully overwritten — silence where no clip plays).
    /// </summary>
    public void MixInto(Span<float> destinationInterleaved, Timecode timelineStart, Project project) =>
        MixInto(destinationInterleaved, timelineStart, project, project.ActiveSequence);

    /// <summary>
    /// Mixes a specific <paramref name="sequence"/>'s audio (rather than the project's active sequence) into
    /// <paramref name="destinationInterleaved"/> — export can render any sequence (PLAN.md step 29 export queue).
    /// </summary>
    public void MixInto(Span<float> destinationInterleaved, Timecode timelineStart, Project project, Sequence sequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        destinationInterleaved.Clear();

        int frames = destinationInterleaved.Length / Channels;
        if (frames == 0)
            return;

        Timecode duration = Timecode.FromSamples(frames, SampleRate);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, sequence, timelineStart, duration);

        // Sum the plan (recursing into nested sub-mixes), applying each plan's master gain; then hard-limit once
        // at the very top so nested layers aren't clamped (and distorted) before they're summed.
        MixPlanInto(destinationInterleaved, plan, frames, depth: 0);
        HardLimit(destinationInterleaved);
    }

    /// <summary>
    /// Sums one buffer plan into <paramref name="dest"/> (overwritten): each media layer's PCM (with its gain
    /// ramp) plus each nested-sequence layer's recursively-mixed sub-mix (PLAN.md step 23), then scales by the
    /// plan's master gain. No hard-limit here — that happens once at the top so nested sub-mixes sum cleanly.
    /// </summary>
    private void MixPlanInto(Span<float> dest, AudioBufferPlan plan, int frames, int depth)
    {
        dest.Clear();
        Span<float> layer = GetScratch(depth, frames * Channels);

        foreach (AudioLayer al in plan.Layers)
        {
            if (al.NestedPlan is { } nested)
            {
                // A nested-sequence layer: mix the child plan into the per-depth scratch, then sum it in under
                // this layer's (the nesting clip's) gain envelope. (Retimed nested audio plays at 1× — step 23.)
                MixPlanInto(layer, nested, frames, depth + 1);
                SumWithRamp(dest, layer, frames, al.GainStartLinear, al.GainEndLinear);
                continue;
            }

            IPcmReader? reader = ResolvePositioned(al.MediaRefId, al.SourceStart);
            if (reader is null)
                continue;

            layer.Clear();
            SourceState state = _states[al.MediaRefId];
            // Speed 1/1 is the common case: read sequentially, no resample (the original fast path, untouched).
            if (al.SpeedRatio.Num == al.SpeedRatio.Den)
            {
                int got = reader.Read(layer);
                state.NextSourceTime += Timecode.FromSamples(got, SampleRate);
            }
            else
            {
                ReadResampled(state, layer, frames, al.SpeedRatio.ToDouble());
            }

            SumWithRamp(dest, layer, frames, al.GainStartLinear, al.GainEndLinear);
        }

        ApplyGain(dest, plan.MasterGainLinear);
    }

    /// <summary>Resolves the reader for a media id and seeks it if the requested source time has jumped.</summary>
    private IPcmReader? ResolvePositioned(MediaRefId id, Timecode sourceStart)
    {
        if (!_states.TryGetValue(id, out SourceState? state))
        {
            IPcmReader? reader = _resolve(id);
            if (reader is null)
                return null;
            state = new SourceState { Reader = reader };
            _states[id] = state;
        }

        if (!state.Positioned || Math.Abs(state.NextSourceTime.Ticks - sourceStart.Ticks) > SeekToleranceTicks)
        {
            state.Reader.SeekTo(sourceStart);
            state.NextSourceTime = sourceStart;
            state.Positioned = true;
            state.ResetResampler(); // the carried resample window is stale after a jump
        }
        return state.Reader;
    }

    /// <summary>
    /// Fills <paramref name="layer"/> (one buffer of <paramref name="frames"/> output sample-frames) by reading
    /// the source at <paramref name="speed"/>× through a streaming linear resampler (PLAN.md step 21). The state's
    /// <see cref="SourceState.Window"/> carries source frames already pulled but not yet consumed, so reads stay
    /// sequential across buffers (no per-buffer seek) and the source cursor never drifts. Pitch is not preserved
    /// (a deliberate first cut — pitch-preserving time-stretch is step 31). End of stream resamples as silence.
    /// </summary>
    private void ReadResampled(SourceState state, Span<float> layer, int frames, double speed)
    {
        int c = Channels;

        // Continuous source index (measured from Window[0]) of the last output sample's right interpolation
        // neighbour, and of the next buffer's first output sample (which fixes how far to advance the window).
        double endPos = state.Phase + frames * speed;
        int rightNeeded = (int)Math.Floor(state.Phase + (frames - 1) * speed) + 1;
        int baseAdvance = (int)Math.Floor(endPos);
        int framesNeeded = Math.Max(rightNeeded, baseAdvance) + 1; // +1 so index `rightNeeded` is in range

        // Pull source frames sequentially until the window holds what this buffer needs. A short read is EOF:
        // zero the tail so out-of-range samples read as silence, but still count them so we don't busy-read.
        EnsureWindow(state, framesNeeded * c);
        if (state.WindowFrames < framesNeeded)
        {
            int toRead = framesNeeded - state.WindowFrames;
            int got = state.Reader.Read(state.Window.AsSpan(state.WindowFrames * c, toRead * c));
            if (got < toRead)
                Array.Clear(state.Window, (state.WindowFrames + got) * c, (toRead - got) * c);
            state.WindowFrames = framesNeeded;
        }

        for (int f = 0; f < frames; f++)
        {
            double pos = state.Phase + f * speed;
            int k = (int)Math.Floor(pos);
            float frac = (float)(pos - k);
            int leftBase = k * c;
            int rightBase = (k + 1) * c;
            for (int ch = 0; ch < c; ch++)
            {
                float left = state.Window[leftBase + ch];
                float right = state.Window[rightBase + ch];
                layer[f * c + ch] = left + (right - left) * frac;
            }
        }

        // Advance the window: drop the consumed frames from the front, carry the rest, keep the fractional phase.
        int drop = Math.Min(baseAdvance, state.WindowFrames);
        int remaining = state.WindowFrames - drop;
        if (remaining > 0 && drop > 0)
            Array.Copy(state.Window, drop * c, state.Window, 0, remaining * c);
        state.WindowFrames = remaining;
        state.Phase = endPos - baseAdvance;
        state.NextSourceTime += Timecode.FromSamples(drop, SampleRate);
    }

    private static void EnsureWindow(SourceState state, int floats)
    {
        if (state.Window.Length < floats)
            Array.Resize(ref state.Window, floats);
    }

    /// <summary>Sums <paramref name="layer"/> into <paramref name="mix"/>, scaling by a per-frame gain that
    /// ramps linearly from <paramref name="gainStart"/> to <paramref name="gainEnd"/> across the buffer.</summary>
    private void SumWithRamp(Span<float> mix, ReadOnlySpan<float> layer, int frames, double gainStart, double gainEnd)
    {
        double step = frames > 1 ? (gainEnd - gainStart) / frames : 0.0;
        int c = Channels;
        for (int f = 0; f < frames; f++)
        {
            float gain = (float)(gainStart + step * f);
            int baseIndex = f * c;
            for (int ch = 0; ch < c; ch++)
                mix[baseIndex + ch] += layer[baseIndex + ch] * gain;
        }
    }

    /// <summary>Scales the buffer by a (master) gain, vectorised over the whole buffer (§6 SIMD). No clamp —
    /// nested sub-mixes must sum un-clamped; the single hard-limit happens once at the top.</summary>
    private static void ApplyGain(Span<float> mix, double gainLinear)
    {
        if (gainLinear == 1.0)
            return; // unity (the common nested case) — nothing to do

        var gain = (float)gainLinear;
        int width = Vector<float>.Count;
        var gainVec = new Vector<float>(gain);

        int i = 0;
        for (; i <= mix.Length - width; i += width)
        {
            var v = new Vector<float>(mix.Slice(i, width)) * gainVec;
            v.CopyTo(mix.Slice(i, width));
        }
        for (; i < mix.Length; i++)
            mix[i] *= gain;
    }

    /// <summary>Hard-limits the final mix to [-1, 1], vectorised over the whole buffer (§6 SIMD).</summary>
    private static void HardLimit(Span<float> mix)
    {
        int width = Vector<float>.Count;
        var lo = new Vector<float>(-1f);
        var hi = new Vector<float>(1f);

        int i = 0;
        for (; i <= mix.Length - width; i += width)
        {
            var v = Vector.Max(lo, Vector.Min(hi, new Vector<float>(mix.Slice(i, width))));
            v.CopyTo(mix.Slice(i, width));
        }
        for (; i < mix.Length; i++)
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
    }

    /// <summary>Returns the layer scratch buffer for <paramref name="depth"/>, growing/creating it as needed so
    /// each nesting level has its own (the non-nested top level keeps one buffer, allocation-free in steady state).</summary>
    private Span<float> GetScratch(int depth, int floats)
    {
        while (_scratchByDepth.Count <= depth)
            _scratchByDepth.Add([]);
        if (_scratchByDepth[depth].Length < floats)
            _scratchByDepth[depth] = new float[floats];
        return _scratchByDepth[depth].AsSpan(0, floats);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (SourceState state in _states.Values)
            state.Reader.Dispose();
        _states.Clear();
    }
}
