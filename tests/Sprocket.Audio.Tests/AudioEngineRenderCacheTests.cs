using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// The audio render-cache splice (PLAN.md step 32, ARCHITECTURE.md §20): while a valid cached range covers a
/// feeder buffer the engine replays the cached master mix instead of mixing live; a buffer the cache can't
/// fully cover mixes live. Deterministic via <see cref="FakeAudioOutput"/> + a fake cache.
/// </summary>
public class AudioEngineRenderCacheTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Covers [Start, End) with a constant sample value; refuses partial coverage like the real service.</summary>
    private sealed class FakeAudioCache : IAudioRenderCache
    {
        public Timecode Start = Timecode.Zero;
        public Timecode End = Timecode.FromSeconds(10);
        public float Value = 0.25f;
        public volatile bool Enabled = true;

        public bool TryRead(Timecode start, Span<float> interleaved)
        {
            if (!Enabled)
                return false;
            Timecode end = start + Timecode.FromSamples(interleaved.Length / Channels, Rate);
            if (start < Start || end > End)
                return false;
            interleaved.Fill(Value);
            return true;
        }
    }

    private static Project EmptyProject() =>
        new(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate));

    private static async Task<float[][]> PlayOneBufferAsync(AudioEngine engine, FakeAudioOutput output)
    {
        engine.Start();
        var deadline = DateTime.UtcNow + Timeout;
        while (output.EnqueuedSnapshot().Length == 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("feeder never enqueued");
            await Task.Delay(5);
        }
        engine.Pause();
        return output.EnqueuedSnapshot();
    }

    [Fact]
    public async Task A_Covered_Buffer_Replays_The_Cached_Mix()
    {
        var output = new FakeAudioOutput();
        output.Configure(Rate, Channels);
        // The mixer would produce silence (no readers); the cache produces 0.25 — so any 0.25 buffer came
        // from the cache, not the mixer.
        await using var engine = new AudioEngine(output, new AudioMixer(Rate, Channels, _ => null), EmptyProject(), bufferFrames: 512)
        {
            RenderCache = new FakeAudioCache(),
        };

        float[][] buffers = await PlayOneBufferAsync(engine, output);
        Assert.All(buffers[0], sample => Assert.Equal(0.25f, sample));
    }

    [Fact]
    public async Task A_Buffer_The_Cache_Cannot_Cover_Mixes_Live()
    {
        var output = new FakeAudioOutput();
        output.Configure(Rate, Channels);
        var cache = new FakeAudioCache { Start = Timecode.FromSeconds(5), End = Timecode.FromSeconds(6) };
        await using var engine = new AudioEngine(output, new AudioMixer(Rate, Channels, _ => null), EmptyProject(), bufferFrames: 512)
        {
            RenderCache = cache,
        };

        // Playback starts at 0 — outside the cached range — so the live mixer (silence) fills the buffer.
        float[][] buffers = await PlayOneBufferAsync(engine, output);
        Assert.All(buffers[0], sample => Assert.Equal(0f, sample));
    }
}
