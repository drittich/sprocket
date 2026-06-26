using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Generates media-bin thumbnails (PLAN.md step 15, UI.md §3.3): a poster frame for video sources and a
/// waveform image for audio. Each is produced once on a background thread (decode is slow and must not block
/// the UI) and cached by source + size, then handed back as an Avalonia <see cref="Bitmap"/>.
/// </summary>
/// <remarks>
/// Decoding a single poster frame / a stretch of PCM and rasterising it once is a one-off cost, NOT the
/// per-frame render hot path, so the no-managed-pixels rule (ARCHITECTURE.md §1) does not apply here — a
/// thumbnail is deliberately copied into a small managed bitmap. Poster decode forces the software path
/// (<see cref="HardwareAccelMode.Disabled"/>) so it is deterministic and carries no GPU dependency.
/// </remarks>
public sealed class ThumbnailService : IDisposable
{
    // Background colour behind letterboxed poster frames (matches the panel's raised surface).
    private static readonly SKColor PosterBg = new(0x22, 0x22, 0x2B);
    private static readonly SKColor WaveBg = new(0x22, 0x22, 0x2B);
    private static readonly SKColor WaveFill = new(0x4F, 0x7A, 0x60);

    // At most this many mono samples are read for a waveform; longer audio is summarised from the lead-in.
    private const int MaxWaveformSamples = 4_000_000;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private volatile bool _disposed;

    /// <summary>
    /// Returns the poster-frame thumbnail for a video source, scaled to fit <paramref name="width"/>×
    /// <paramref name="height"/> (letterboxed). Returns <see langword="null"/> for an offline / no-video source
    /// or on any decode failure. Cached; concurrent callers share one decode.
    /// </summary>
    public Task<Bitmap?> GetPosterAsync(MediaRef media, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed || !media.Info.HasVideo)
            return Task.FromResult<Bitmap?>(null);

        string key = $"poster:{media.Id}:{width}x{height}";
        return _cache.GetOrAdd(key, _ => Task.Run(() => RenderPoster(media, width, height)));
    }

    /// <summary>
    /// Returns the waveform thumbnail for an audio source at <paramref name="width"/>×<paramref name="height"/>.
    /// Returns <see langword="null"/> for an offline / no-audio source or on failure. Cached.
    /// </summary>
    public Task<Bitmap?> GetWaveformAsync(MediaRef media, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed || !media.Info.HasAudio)
            return Task.FromResult<Bitmap?>(null);

        string key = $"wave:{media.Id}:{width}x{height}";
        return _cache.GetOrAdd(key, _ => Task.Run(() => RenderWaveform(media, width, height)));
    }

    private static Bitmap? RenderPoster(MediaRef media, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        try
        {
            using MediaSource source = MediaSource.Open(media.AbsolutePath, HardwareAccelMode.Disabled);
            using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);

            // Seek a little into the clip for a representative poster (avoids a black/leader first frame).
            Timecode poster = Timecode.Min(Timecode.FromSeconds(1), new Timecode(media.Info.Duration.Ticks / 2));
            if (poster.Ticks > 0)
                source.SeekTo(poster);

            if (!source.TryDecodeNextFrame(pool, out VideoFrame? frame))
                return null;

            using (frame)
            {
                var dstInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using SKSurface surface = SKSurface.Create(dstInfo);
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(PosterBg);

                var srcInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using SKImage src = SKImage.FromPixels(srcInfo, frame.Pixels, frame.RowBytes);
                SKRect dest = FramePresenter.ComputeFitRect(SKRect.Create(width, height), frame.Width, frame.Height);
                canvas.DrawImage(src, dest, new SKSamplingOptions(SKFilterMode.Linear));

                return Encode(surface);
            }
        }
        catch
        {
            return null; // offline media / unsupported codec — the tile shows a fallback (§15)
        }
    }

    private static Bitmap? RenderWaveform(MediaRef media, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        try
        {
            int sampleRate = media.Info.SampleRate > 0 ? media.Info.SampleRate : 48000;
            using IPcmReader reader = AudioSource.Open(media.AbsolutePath, sampleRate, channels: 1);

            // Read up to a bounded number of mono samples, then reduce to one peak per output column.
            var samples = new List<float>();
            var chunk = new float[8192];
            int n;
            while (samples.Count < MaxWaveformSamples && (n = reader.Read(chunk)) > 0)
            {
                for (int i = 0; i < n; i++)
                    samples.Add(chunk[i]);
            }

            float[] peaks = WaveformBuilder.BuildPeaks(samples, channels: 1, bucketCount: Math.Max(1, width));
            return DrawWaveform(peaks, width, height);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap DrawWaveform(float[] peaks, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(WaveBg);

        float mid = height / 2f;
        float maxAmp = mid - 2;
        using var paint = new SKPaint { Color = WaveFill, IsAntialias = false, StrokeWidth = 1 };
        for (int x = 0; x < peaks.Length && x < width; x++)
        {
            float amp = Math.Clamp(peaks[x], 0f, 1f) * maxAmp;
            canvas.DrawLine(x + 0.5f, mid - amp, x + 0.5f, mid + amp, paint);
        }

        // A faint centre line so silent regions still read as a waveform.
        using var centre = new SKPaint { Color = WaveFill.WithAlpha(80), StrokeWidth = 1 };
        canvas.DrawLine(0, mid, width, mid, centre);

        return Encode(surface)!;
    }

    private static Bitmap Encode(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    /// <summary>Disposes every cached bitmap. Pending decodes complete and their results are dropped.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Task<Bitmap?> task in _cache.Values)
        {
            if (task.IsCompletedSuccessfully)
                task.Result?.Dispose();
        }
        _cache.Clear();
    }
}
