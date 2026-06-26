using System;
using System.Collections.Generic;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Reduces interleaved PCM to a small array of per-bucket amplitude peaks for drawing a waveform thumbnail
/// (PLAN.md step 15, UI.md §3.3). Pure and allocation-light so it is unit-testable without FFmpeg or a UI; the
/// <see cref="ThumbnailService"/> reads PCM through an <c>IPcmReader</c> and feeds it here, and the tile draws
/// the returned peaks as mirrored bars around a centre line.
/// </summary>
public static class WaveformBuilder
{
    /// <summary>
    /// Computes <paramref name="bucketCount"/> peaks in [0, 1] from interleaved float PCM. Each output bucket is
    /// the maximum absolute mono-mixed sample over its slice of the timeline. Returns an all-zero array when
    /// there is no audio. <paramref name="channels"/> must be ≥ 1.
    /// </summary>
    public static float[] BuildPeaks(IReadOnlyList<float> interleaved, int channels, int bucketCount)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be at least 1.");
        if (bucketCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be at least 1.");

        var peaks = new float[bucketCount];
        int frames = interleaved.Count / channels;
        if (frames == 0)
            return peaks;

        for (int frame = 0; frame < frames; frame++)
        {
            // Mono-mix this frame (average of channels) and track the peak in its bucket.
            double sum = 0;
            int baseIndex = frame * channels;
            for (int c = 0; c < channels; c++)
                sum += interleaved[baseIndex + c];
            float mono = (float)Math.Abs(sum / channels);

            // Map the frame to a bucket; the last frame maps to the last bucket exactly.
            int bucket = (int)((long)frame * bucketCount / frames);
            if (bucket >= bucketCount)
                bucket = bucketCount - 1;
            if (mono > peaks[bucket])
                peaks[bucket] = mono;
        }

        return peaks;
    }
}
