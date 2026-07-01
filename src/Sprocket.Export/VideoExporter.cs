using SkiaSharp;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

namespace Sprocket.Export;

/// <summary>Tunables for an export. The defaults produce a CRF-quality H.264 + AAC MP4 at the project's format,
/// so <c>default(ExportOptions)</c> reproduces the step-8 behaviour; set <see cref="Format"/> for the wider
/// container/codec matrix (PLAN.md step 27).</summary>
/// <param name="Format">The container × video-codec × audio-codec to deliver. <c>default</c> is MP4 / H.264 / AAC.</param>
/// <param name="Quality">The constant-quality (CRF) tier used when no explicit bit rate is set.</param>
/// <param name="Channels">Audio channel count to render and encode (default stereo).</param>
/// <param name="VideoBitRate">Target video bit rate in bits/s, or <c>0</c> for CRF-quality encoding.</param>
/// <param name="AudioBitRate">Target audio bit rate in bits/s, or <c>0</c> for the encoder default.</param>
/// <param name="GopSize">Keyframe interval in frames, or <c>0</c> for the encoder default.</param>
/// <param name="PixelFormat">An explicit encoder pixel-format name, or <see langword="null"/> to use the codec's
/// default (yuv420p for most; yuv422p10le for ProRes).</param>
public readonly record struct ExportOptions(
    ExportFormat Format = default,
    ExportQuality Quality = ExportQuality.High,
    int Channels = 2,
    long VideoBitRate = 0,
    long AudioBitRate = 0,
    int GopSize = 0,
    string? PixelFormat = null);

/// <summary>
/// Renders a <see cref="Project"/> offline to a full-resolution movie in the chosen container/codec matrix
/// (PLAN.md step 8 + step 27). This is the export half of "the same render graph serves preview and export"
/// (ARCHITECTURE.md §5): for each output frame it resolves the <see cref="VideoFramePlan"/> with
/// <see cref="RenderGraph"/> — exactly as the preview does — composites the layers onto an offscreen Skia surface
/// with the step-7 effect shaders, reads the pixels back, and hands them to the <see cref="MediaEncoder"/>. Audio
/// is mixed by <see cref="AudioMixer"/> over the same timeline and encoded alongside, interleaved by output
/// timestamp. Only the muxer/encoder back end changes with <see cref="ExportOptions.Format"/> — the render is
/// identical, so the export stays deterministic (§5/§17).
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
    /// Exports the project's active sequence to <paramref name="outputPath"/>. Reports progress in [0, 1] over the
    /// timeline and honours <paramref name="cancellationToken"/> between frames. Throws
    /// <see cref="ArgumentException"/> for an empty timeline.
    /// </summary>
    public static void Export(
        Project project,
        string outputPath,
        ExportOptions options = default,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => Export(project, outputPath, options, sequenceId: null, range: null, progress, cancellationToken);

    /// <summary>
    /// Exports one sequence — or a sub-range of it — to <paramref name="outputPath"/> (PLAN.md step 29 export queue).
    /// <paramref name="sequenceId"/> selects which sequence (<see langword="null"/> = the project's active sequence);
    /// <paramref name="range"/> selects a half-open <c>[In, Out)</c> timeline slice (<see langword="null"/> = the
    /// whole timeline). The exported file's own timestamps start at zero regardless of the range start. Reports
    /// progress in [0, 1] over the exported range and honours <paramref name="cancellationToken"/> between frames.
    /// </summary>
    public static void Export(
        Project project,
        string outputPath,
        ExportOptions options,
        SequenceId? sequenceId,
        ExportRange? range,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        Sequence sequence = sequenceId is { } id
            ? project.GetSequence(id) ?? throw new ArgumentException($"No sequence with id {id} in the project.", nameof(sequenceId))
            : project.ActiveSequence;

        // `default(ExportOptions)` leaves Channels = 0; treat that as the documented stereo default.
        int channels = options.Channels > 0 ? options.Channels : 2;

        ExportFormat format = options.Format;
        if (!format.IsValid)
            throw new ArgumentException(
                $"{ExportCodecs.Video(format.VideoCodec).DisplayName} / {ExportCodecs.Audio(format.AudioCodec).DisplayName} " +
                $"is not a valid combination for the {ExportCodecs.Container(format.Container).DisplayName} container.",
                nameof(options));

        Timeline timeline = sequence.Timeline;
        Timecode fullDuration = timeline.Duration;
        if (fullDuration <= Timecode.Zero)
            throw new ArgumentException("The timeline is empty — nothing to export.", nameof(project));

        Rational fps = timeline.FrameRate;
        if (fps.Num <= 0 || fps.Den <= 0)
            throw new ArgumentException("The timeline has no valid frame rate.", nameof(project));

        // Resolve the export sub-range, clamped to the sequence. The frame/sample loops below run over the range
        // duration, sampling the timeline at rangeIn + offset; encoder timestamps start at zero (a slice becomes a
        // file that plays from 0). A null range exports the whole timeline (the pre-step-29 behaviour).
        ExportRange effectiveRange = (range ?? ExportRange.Whole(fullDuration)).ClampTo(fullDuration);
        Timecode rangeIn = effectiveRange.In;
        Timecode duration = effectiveRange.Duration;
        if (duration <= Timecode.Zero)
            throw new ArgumentException("The export range is empty — nothing to export.", nameof(range));

        // Cap the delivery resolution at 4K (export-side limit only — the timeline/canvas are unrestricted,
        // PLAN.md step 27), scaling down to fit while preserving aspect. Then round down to even (≤ 1px) so a
        // 4:2:0 codec accepts an odd-sized timeline. The offscreen surface uses the same size — render and encode agree.
        (int outWidth, int outHeight) = ComputeExportResolution(timeline.Resolution.Width, timeline.Resolution.Height);
        if (outWidth <= 0 || outHeight <= 0)
            throw new ArgumentException("The timeline resolution is too small to export.", nameof(project));
        int sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 48000;

        bool wantAudio = HasAudibleAudio(project, sequence);

        VideoCodecInfo videoCodec = ExportCodecs.Video(format.VideoCodec);
        var video = new VideoEncoderSettings(
            outWidth, outHeight, fps,
            CodecName: videoCodec.EncoderName,
            PixelFormat: options.PixelFormat ?? videoCodec.PixelFormat,
            BitRate: options.VideoBitRate,
            GopSize: options.GopSize,
            Crf: ExportCodecs.CrfFor(format.VideoCodec, options.Quality),
            Preset: videoCodec.DefaultPreset);

        AudioEncoderSettings? audio = wantAudio
            ? new AudioEncoderSettings(sampleRate, channels, ExportCodecs.Audio(format.AudioCodec).EncoderName, options.AudioBitRate)
            : null;

        var providers = new Dictionary<MediaRefId, ExportFrameProvider?>();
        AudioMixer? mixer = null;
        MediaEncoder? encoder = null;
        SKSurface? surface = null;
        SkiaEffectPipeline? pipeline = null;
        float[] mixBuffer = [];
        bool completed = false;

        try
        {
            encoder = MediaEncoder.Create(outputPath, video, audio, format.MuxerName);

            var info = new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException("Failed to create the offscreen export surface.");
            pipeline = new SkiaEffectPipeline();
            var fullRect = SKRect.Create(0, 0, outWidth, outHeight);

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
                    RenderVideoFrame(project, sequence, rangeIn, nextVideoIndex, fps, surface, pipeline, fullRect, providers);
                    using SKPixmap pixels = surface.PeekPixels();
                    encoder.WriteVideoFrame(pixels.GetPixels(), pixels.RowBytes, nextVideoIndex);
                    nextVideoIndex++;
                }
                else
                {
                    int chunk = (int)Math.Min(encoder.AudioFrameSize, totalSamples - nextSample);
                    Span<float> buffer = mixBuffer.AsSpan(0, chunk * channels);
                    mixer!.MixInto(buffer, rangeIn + Timecode.FromSamples(nextSample, sampleRate), project, sequence);
                    encoder.WriteAudioFrame(buffer, nextSample);
                    nextSample += chunk;
                }

                progress?.Report(ComputeProgress(nextVideoIndex, fps, duration));
            }

            encoder.Finish();
            progress?.Report(1.0);
            completed = true;
        }
        finally
        {
            mixer?.Dispose(); // disposes the audio readers it owns
            foreach (ExportFrameProvider? provider in providers.Values)
                provider?.Dispose();
            pipeline?.Dispose();
            surface?.Dispose();
            encoder?.Dispose();

            // A failed or cancelled export never wrote the MP4 trailer (the moov atom): `MediaEncoder.Finish`
            // writes it, `Dispose` does not. Leaving the file behind hands the caller a full-size but
            // unplayable .mp4, so delete the partial output once the encoder has released its file handle.
            if (!completed)
                TryDelete(outputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup of a partial export; never mask the original failure */ }
    }

    /// <summary>4K delivery cap (PLAN.md step 27): DCI-4K width (UHD 3840 fits) and 4K height. An export-side
    /// limit only — import, the timeline, and the sequence canvas are unrestricted.</summary>
    public const int MaxExportWidth = 4096;
    public const int MaxExportHeight = 2160;

    /// <summary>Computes the encoded resolution for a sequence of <paramref name="width"/>×<paramref name="height"/>:
    /// scaled down (preserving aspect) to fit within the 4K cap when larger, then rounded down to even so a 4:2:0
    /// codec accepts it. Sequences at or below 4K encode at their exact even size. Exposed so the UI can show the
    /// resolution an export will actually produce.</summary>
    public static (int width, int height) ComputeExportResolution(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);

        double scale = Math.Min(1.0, Math.Min((double)MaxExportWidth / width, (double)MaxExportHeight / height));
        int w = (int)Math.Round(width * scale);
        int h = (int)Math.Round(height * scale);
        // Clamp against the cap (rounding can nudge to cap+1) then force even.
        w = Math.Min(w, MaxExportWidth) & ~1;
        h = Math.Min(h, MaxExportHeight) & ~1;
        return (w, h);
    }

    /// <summary>Composites the frame at output index <paramref name="frameIndex"/> onto <paramref name="surface"/>:
    /// clear to black, then draw each resolved layer bottom→top with its effect chain, opacity, and blend. The
    /// timeline time sampled is <paramref name="rangeIn"/> + the frame's offset, so a sub-range export starts at the
    /// range's in-point while the output frame index (and thus the file) starts at zero.</summary>
    private static void RenderVideoFrame(
        Project project, Sequence sequence, Timecode rangeIn, long frameIndex, Rational fps,
        SKSurface surface, SkiaEffectPipeline pipeline, SKRect fullRect,
        Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        Timecode t = rangeIn + Timecode.FromFrames(frameIndex, fps);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, sequence, t);

        surface.Canvas.Clear(SKColors.Black);
        CompositePlan(project, plan, surface, pipeline, fullRect, providers);
        surface.Canvas.Flush();
    }

    /// <summary>
    /// Composites one resolved plan onto <paramref name="surface"/> (already cleared by the caller): each layer
    /// bottom→top with its effect chain, opacity, and blend. A nested-sequence layer renders its child plan into a
    /// transparent offscreen surface and composites the result like any image layer (PLAN.md step 23) — the
    /// recursive "the graph turns a (timeline, t) into a frame" rule, on the deterministic export path.
    /// </summary>
    private static void CompositePlan(
        Project project, VideoFramePlan plan,
        SKSurface surface, SkiaEffectPipeline pipeline, SKRect bounds,
        Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        SKCanvas canvas = surface.Canvas;
        foreach (VideoLayer layer in plan.Layers)
        {
            switch (layer.Kind)
            {
                case LayerKind.Generator:
                    // A generator fills the sequence frame; render at full resolution then composite (PLAN.md step 19).
                    pipeline.DrawGenerator(
                        canvas, bounds, layer.Generator!, (int)bounds.Width, (int)bounds.Height,
                        layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                    break;

                case LayerKind.Adjustment:
                    // Apply the adjustment's effects to everything composited beneath it (PLAN.md step 19).
                    pipeline.DrawAdjustment(surface, bounds, layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                    break;

                case LayerKind.Sequence:
                {
                    // Render the child sequence into its own transparent surface, then composite it like any image
                    // layer with this (nesting) clip's effect chain / opacity / blend (PLAN.md step 23).
                    if (RenderNestedSequence(project, layer.NestedPlan!, pipeline, providers) is not { } nestedImage)
                        break;
                    using (nestedImage)
                    {
                        SKRect dest = FramePresenter.ComputeFitRect(bounds, nestedImage.Width, nestedImage.Height);
                        pipeline.DrawImageLayer(canvas, dest, nestedImage, layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                    }
                    break;
                }

                case LayerKind.Transition:
                {
                    // Blend the two clips' frames per the transition (PLAN.md step 25). Each side's content is
                    // snapshotted into an independent image first, so a transition between two clips of the SAME
                    // source (one provider) doesn't have its first frame recycled by the second decode.
                    ResolvedTransition tr = layer.Transition!;
                    SKImage? fromImg = RenderSideContent(project, tr.From, pipeline, bounds, providers);
                    SKImage? toImg = RenderSideContent(project, tr.To, pipeline, bounds, providers);
                    SKBlendMode blend = ToBlendMode(layer.BlendMode);
                    try
                    {
                        // If a side is missing (offline/empty), composite the other on its own rather than failing.
                        if (fromImg is null && toImg is null)
                            break;
                        if (fromImg is null)
                        {
                            DrawSide(canvas, bounds, toImg!, tr.To.Effects, pipeline, layer.Opacity, blend);
                            break;
                        }
                        if (toImg is null)
                        {
                            DrawSide(canvas, bounds, fromImg, tr.From.Effects, pipeline, layer.Opacity, blend);
                            break;
                        }
                        pipeline.DrawTransition(
                            canvas, bounds, fromImg, tr.From.Effects, toImg, tr.To.Effects, tr, layer.Opacity, blend);
                    }
                    finally
                    {
                        fromImg?.Dispose();
                        toImg?.Dispose();
                    }
                    break;
                }

                default:
                {
                    ExportFrameProvider? provider = ResolveProvider(project, layer.MediaRefId, providers);
                    VideoFrame? frame = provider?.GetFrame(layer.SourceTime);
                    if (frame is null)
                        continue;

                    SKRect dest = FramePresenter.ComputeFitRect(bounds, frame.Width, frame.Height);
                    pipeline.DrawLayer(
                        canvas, dest, frame.Pixels, frame.RowBytes, frame.Width, frame.Height,
                        layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode), frame.HasAlpha);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Renders one side of a transition to a standalone content-only <see cref="SKImage"/> (no effects — those are
    /// applied by <see cref="SkiaEffectPipeline.DrawTransition"/>), or <see langword="null"/> when it produces no
    /// pixels (an offline media source / empty nested sequence). A media side is copied out of the decoder's buffer
    /// (<see cref="SKImage.FromPixelCopy(SKImageInfo, nint, int)"/>) so both sides stay valid even when they share a
    /// source/provider (PLAN.md step 25).
    /// </summary>
    private static SKImage? RenderSideContent(
        Project project, VideoLayer side, SkiaEffectPipeline pipeline, SKRect bounds,
        Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        switch (side.Kind)
        {
            case LayerKind.Generator:
            {
                int w = Math.Max(1, (int)bounds.Width);
                int h = Math.Max(1, (int)bounds.Height);
                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                using SKSurface? offscreen = SKSurface.Create(info);
                if (offscreen is null)
                    return null;
                offscreen.Canvas.Clear(SKColors.Transparent);
                pipeline.DrawGenerator(offscreen.Canvas, SKRect.Create(0, 0, w, h), side.Generator!, w, h, []);
                offscreen.Canvas.Flush();
                return offscreen.Snapshot();
            }

            case LayerKind.Sequence:
                return RenderNestedSequence(project, side.NestedPlan!, pipeline, providers);

            default:
            {
                ExportFrameProvider? provider = ResolveProvider(project, side.MediaRefId, providers);
                VideoFrame? frame = provider?.GetFrame(side.SourceTime);
                if (frame is null)
                    return null;
                // Alpha sides copy out as straight (unpremultiplied) RGBA so the transition blend composites them
                // premultiplied-correctly; opaque sides stay Opaque (the alpha bytes are ignored). PLAN.md step 26.
                SKAlphaType alphaType = frame.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
                var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, alphaType);
                return SKImage.FromPixelCopy(info, frame.Pixels, frame.RowBytes);
            }
        }
    }

    /// <summary>Composites one transition side's image on its own (fit-letterboxed) — the graceful path when the
    /// other side produced no pixels.</summary>
    private static void DrawSide(
        SKCanvas canvas, SKRect bounds, SKImage image, IReadOnlyList<ResolvedEffect> effects,
        SkiaEffectPipeline pipeline, double opacity, SKBlendMode blend)
    {
        SKRect dest = FramePresenter.ComputeFitRect(bounds, image.Width, image.Height);
        pipeline.DrawImageLayer(canvas, dest, image, effects, opacity, blend);
    }

    /// <summary>Renders a nested sequence's plan to a transparent offscreen <see cref="SKImage"/> at the child
    /// sequence's resolution (recursing for deeper nests), or <see langword="null"/> if the surface can't be made.</summary>
    private static SKImage? RenderNestedSequence(
        Project project, VideoFramePlan nestedPlan,
        SkiaEffectPipeline pipeline, Dictionary<MediaRefId, ExportFrameProvider?> providers)
    {
        int w = Math.Max(1, nestedPlan.Resolution.Width);
        int h = Math.Max(1, nestedPlan.Resolution.Height);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface? nested = SKSurface.Create(info);
        if (nested is null)
            return null;

        nested.Canvas.Clear(SKColors.Transparent); // transparent so empty child areas reveal the parent's lower layers
        CompositePlan(project, nestedPlan, nested, pipeline, SKRect.Create(0, 0, w, h), providers);
        nested.Canvas.Flush();
        return nested.Snapshot();
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

    /// <summary>Whether the export will have audible audio — any enabled audio track (in the exported
    /// <paramref name="sequence"/> or, recursively, a nested one) carrying a clip whose source has audio
    /// (PLAN.md step 23).</summary>
    private static bool HasAudibleAudio(Project project, Sequence sequence) =>
        SequenceHasAudio(project, sequence, []);

    private static bool SequenceHasAudio(Project project, Sequence sequence, HashSet<SequenceId> path)
    {
        if (!path.Add(sequence.Id))
            return false; // cycle guard
        try
        {
            foreach (AudioTrack track in sequence.Timeline.AudioTracks)
            {
                if (!track.Enabled)
                    continue;
                foreach (Clip clip in track.Clips)
                {
                    if (clip.Kind == ClipKind.Sequence)
                    {
                        if (clip.SourceSequenceId is { } id && project.GetSequence(id) is { } child
                            && SequenceHasAudio(project, child, path))
                            return true;
                    }
                    else if (project.MediaPool.Get(clip.MediaRefId) is { Info.HasAudio: true })
                        return true;
                }
            }
            return false;
        }
        finally
        {
            path.Remove(sequence.Id);
        }
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
