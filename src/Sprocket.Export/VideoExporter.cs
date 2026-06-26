using SkiaSharp;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

namespace Sprocket.Export;

/// <summary>Tunables for an export. The defaults produce a CRF-quality H.264 + AAC MP4 at the project's format.</summary>
/// <param name="Channels">Audio channel count to render and encode (default stereo).</param>
/// <param name="VideoBitRate">Target video bit rate in bits/s, or <c>0</c> for CRF-quality encoding.</param>
/// <param name="AudioBitRate">Target audio bit rate in bits/s, or <c>0</c> for the encoder default.</param>
/// <param name="GopSize">Keyframe interval in frames, or <c>0</c> for the encoder default.</param>
public readonly record struct ExportOptions(
    int Channels = 2,
    long VideoBitRate = 0,
    long AudioBitRate = 0,
    int GopSize = 0);

/// <summary>
/// Renders a <see cref="Project"/> offline to a full-resolution H.264/AAC MP4 (PLAN.md step 8, slice DoD #7).
/// This is the export half of "the same render graph serves preview and export" (ARCHITECTURE.md §5): for each
/// output frame it resolves the <see cref="VideoFramePlan"/> with <see cref="RenderGraph"/> — exactly as the
/// preview does — composites the layers onto an offscreen Skia surface with the step-7 effect shaders, reads the
/// pixels back, and hands them to the <see cref="MediaEncoder"/>. Audio is mixed by <see cref="AudioMixer"/> over
/// the same timeline and encoded alongside, interleaved by output timestamp.
/// </summary>
/// <remarks>
/// <para>Export is throughput-bound, not real-time: it renders to a <b>raster</b> Skia surface (deterministic,
/// no GPU/display needed) and decodes sources in software at <b>full resolution</b> (never proxies, §17). The
/// determinism is what makes golden-frame export testing possible. The render is single-threaded for the slice;
/// the parallel decode→effect→encode pipeline the architecture allows is a later throughput optimization.</para>
/// <para>Frames are pulled lazily per source via <see cref="ExportFrameProvider"/> and the audio readers are
/// owned by the mixer, so a multi-minute timeline streams through with bounded memory. An offline/missing source
/// renders as black / silence rather than failing the export (§15).</para>
/// </remarks>
public static class VideoExporter
{
    /// <summary>
    /// Exports <paramref name="project"/> to <paramref name="outputPath"/> (an <c>.mp4</c>). Reports progress in
    /// [0, 1] over the timeline and honours <paramref name="cancellationToken"/> between frames. Throws
    /// <see cref="ArgumentException"/> for an empty timeline.
    /// </summary>
    public static void Export(
        Project project,
        string outputPath,
        ExportOptions options = default,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // `default(ExportOptions)` leaves Channels = 0; treat that as the documented stereo default.
        int channels = options.Channels > 0 ? options.Channels : 2;

        Timeline timeline = project.Timeline;
        Timecode duration = timeline.Duration;
        if (duration <= Timecode.Zero)
            throw new ArgumentException("The timeline is empty — nothing to export.", nameof(project));

        Rational fps = timeline.FrameRate;
        if (fps.Num <= 0 || fps.Den <= 0)
            throw new ArgumentException("The timeline has no valid frame rate.", nameof(project));

        Resolution resolution = timeline.Resolution;
        int sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 48000;

        bool wantAudio = HasAudibleAudio(project);

        var video = new VideoEncoderSettings(resolution.Width, resolution.Height, fps, options.VideoBitRate, options.GopSize);
        AudioEncoderSettings? audio = wantAudio
            ? new AudioEncoderSettings(sampleRate, channels, options.AudioBitRate)
            : null;

        var providers = new Dictionary<MediaRefId, ExportFrameProvider?>();
        AudioMixer? mixer = null;
        MediaEncoder? encoder = null;
        SKSurface? surface = null;
        SkiaEffectPipeline? pipeline = null;
        float[] mixBuffer = [];

        try
        {
            encoder = MediaEncoder.Create(outputPath, video, audio);

            var info = new SKImageInfo(resolution.Width, resolution.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException("Failed to create the offscreen export surface.");
            pipeline = new SkiaEffectPipeline();
            var fullRect = SKRect.Create(0, 0, resolution.Width, resolution.Height);

            if (encoder.HasAudio)
            {
                mixer = new AudioMixer(sampleRate, channels, id => OpenPcmReader(project, id, sampleRate, channels));
                mixBuffer = new float[encoder.AudioFrameSize * channels];
            }

            long totalSamples = encoder.HasAudio ? duration.ToSampleIndex(sampleRate) : 0;
            long nextVideoIndex = 0;
            long nextSample = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool videoDone = Timecode.FromFrames(nextVideoIndex, fps) >= duration;
                bool audioDone = !encoder.HasAudio || nextSample >= totalSamples;
                if (videoDone && audioDone)
                    break;

                long videoTick = videoDone ? long.MaxValue : Timecode.FromFrames(nextVideoIndex, fps).Ticks;
                long audioTick = audioDone ? long.MaxValue : Timecode.FromSamples(nextSample, sampleRate).Ticks;

                // Emit whichever stream's next packet sits earlier on the timeline, so the muxer interleaves cleanly.
                if (!videoDone && (audioDone || videoTick <= audioTick))
                {
                    RenderVideoFrame(project, nextVideoIndex, fps, surface, pipeline, fullRect, providers);
                    using SKPixmap pixels = surface.PeekPixels();
                    encoder.WriteVideoFrame(pixels.GetPixels(), pixels.RowBytes, nextVideoIndex);
                    nextVideoIndex++;
                }
                else
                {
                    int chunk = (int)Math.Min(encoder.AudioFrameSize, totalSamples - nextSample);
                    Span<float> buffer = mixBuffer.AsSpan(0, chunk * channels);
                    mixer!.MixInto(buffer, Timecode.FromSamples(nextSample, sampleRate), project);
                    encoder.WriteAudioFrame(buffer, nextSample);
                    nextSample += chunk;
                }

                progress?.Report(ComputeProgress(nextVideoIndex, fps, duration));
            }

            encoder.Finish();
            progress?.Report(1.0);
        }
        finally
        {
            mixer?.Dispose(); // disposes the audio readers it owns
            foreach (ExportFrameProvider? provider in providers.Values)
                provider?.Dispose();
            pipeline?.Dispose();
            surface?.Dispose();
            encoder?.Dispose();
        }
    }

    /// <summary>Composites the frame at index <paramref name="frameIndex"/> onto <paramref name="surface"/>:
    /// clear to black, then draw each resolved layer bottom→top with its effect chain, opacity, and blend.</summary>
    private static void RenderVideoFrame(
        Project project, long frameIndex, Rational fps,
        SKSurface surface, SkiaEffectPipeline pipeline, SKRect fullRect,
        Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        Timecode t = Timecode.FromFrames(frameIndex, fps);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, t);

        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        foreach (VideoLayer layer in plan.Layers)
        {
            ExportFrameProvider? provider = ResolveProvider(project, layer.MediaRefId, providers);
            VideoFrame? frame = provider?.GetFrame(layer.SourceTime);
            if (frame is null)
                continue;

            SKRect dest = FramePresenter.ComputeFitRect(fullRect, frame.Width, frame.Height);
            pipeline.DrawLayer(
                canvas, dest, frame.Pixels, frame.RowBytes, frame.Width, frame.Height,
                layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
        }

        canvas.Flush();
    }

    /// <summary>Resolves (and caches) the full-resolution frame provider for a media id, or <see langword="null"/>
    /// if the source is offline / has no video (it renders as black).</summary>
    private static ExportFrameProvider? ResolveProvider(
        Project project, MediaRefId id, Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        if (providers.TryGetValue(id, out ExportFrameProvider? provider))
            return provider;

        provider = null;
        MediaRef? media = project.MediaPool.Get(id);
        if (media is { Info.HasVideo: true })
        {
            try
            {
                // Software decode for bit-deterministic, GPU-independent export output.
                provider = new ExportFrameProvider(MediaSource.Open(media.AbsolutePath, HardwareAccelMode.Disabled));
            }
            catch
            {
                provider = null; // offline/unreadable source → black frames, don't fail the export (§15)
            }
        }

        providers[id] = provider;
        return provider;
    }

    /// <summary>Opens a PCM reader for the mixer, or <see langword="null"/> (mixed as silence) when the media is
    /// offline / has no audio. The mixer owns and disposes the returned reader.</summary>
    private static IPcmReader? OpenPcmReader(Project project, MediaRefId id, int sampleRate, int channels)
    {
        MediaRef? media = project.MediaPool.Get(id);
        if (media is not { Info.HasAudio: true })
            return null;
        try
        {
            return AudioSource.Open(media.AbsolutePath, sampleRate, channels);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether the project has any audio track carrying at least one clip whose source has audio.</summary>
    private static bool HasAudibleAudio(Project project)
    {
        foreach (AudioTrack track in project.Timeline.AudioTracks)
        {
            if (!track.Enabled)
                continue;
            foreach (Clip clip in track.Clips)
            {
                if (project.MediaPool.Get(clip.MediaRefId) is { Info.HasAudio: true })
                    return true;
            }
        }
        return false;
    }

    private static double ComputeProgress(long nextVideoIndex, Rational fps, Timecode duration)
    {
        double done = Timecode.FromFrames(nextVideoIndex, fps).ToSeconds();
        double total = duration.ToSeconds();
        return total <= 0 ? 1.0 : Math.Clamp(done / total, 0.0, 1.0);
    }

    private static SKBlendMode ToBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Add => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };
}
