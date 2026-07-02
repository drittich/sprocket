using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Export;

/// <summary>
/// Renders the preview cache's intermediates (ARCHITECTURE.md §20, PLAN.md step 32): a sequence range's
/// composited video to a <b>speed-first all-intra</b> file, and its master audio mix to <b>uncompressed
/// float32 WAV</b>. Both run the identical deterministic offline pipeline export uses — the same render graph,
/// the same full-resolution sources (never proxies) — so a cached range replays exactly what live compositing
/// would show. Only the encoding differs from delivery: all-intra + ultrafast + hardware-candidates because the
/// cache is decoded on the scrubbing hot path and is local/regenerable, never shipped (§11 "Preview vs. delivery
/// codecs"). Export itself never reads these files (§17).
/// </summary>
/// <remarks>Like export, this drives an in-process libav muxer (video side): the caller must quiesce all other
/// in-process decode pipelines first (<c>PlaybackEngine.SuspendAsync</c> — the hazard <c>ProxyTranscoder</c>
/// documents). The audio side is pure managed I/O over decode-only sources.</remarks>
public static class PreviewRenderer
{
    /// <summary>The video intermediate's file extension (an MP4 container).</summary>
    public const string VideoExtension = ".mp4";

    /// <summary>The audio intermediate's file extension (a float32 WAV).</summary>
    public const string AudioExtension = ".wav";

    /// <summary>
    /// The encoder settings for a preview video intermediate: H.264 in MP4 with <b>GOP 1 (all-intra)</b> so any
    /// frame decodes without reference chains (instant scrub), the <b>ultrafast</b> software preset (encode speed
    /// over size — the cache is local and regenerable), and the platform's <b>hardware encoders probed first</b>
    /// (NVENC/QSV/AMF on Windows, VideoToolbox on macOS, VAAPI on Linux) with the software encoder as the
    /// guaranteed fallback — the OS-varying speed-first policy of §11, harmless here because the cache never
    /// affects export. Video only: the audio side is cached separately as PCM.
    /// </summary>
    public static ExportOptions VideoOptions => new(
        Quality: ExportQuality.High,
        GopSize: 1,
        Preset: "ultrafast",
        VideoOnly: true,
        Acceleration: ExportAcceleration.Hardware);

    /// <summary>
    /// Renders the composited video of <paramref name="range"/> in sequence <paramref name="sequenceId"/>
    /// (<see langword="null"/> = the active sequence) to the all-intra intermediate at <paramref name="outputPath"/>.
    /// Progress/cancellation as in <see cref="VideoExporter.Export(Project, string, ExportOptions, SequenceId?, ExportRange?, IProgress{double}?, CancellationToken)"/>;
    /// a cancelled/failed render deletes the partial file.
    /// </summary>
    public static void RenderVideo(
        Project project, SequenceId? sequenceId, ExportRange range, string outputPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => VideoExporter.Export(project, outputPath, VideoOptions, sequenceId, range, progress, cancellationToken);

    /// <summary>
    /// Renders the master audio mix of <paramref name="range"/> in sequence <paramref name="sequenceId"/>
    /// (<see langword="null"/> = the active sequence) to a float32 WAV at <paramref name="outputPath"/> — stereo at
    /// the sequence sample rate, the mixer's native format, so the cache is bit-identical to a live mix of the same
    /// state. <paramref name="effectFactory"/> lets the composition root supply plugin audio effects (step 33) so a
    /// frozen chain includes them. A cancelled/failed render deletes the partial file.
    /// </summary>
    public static void RenderAudio(
        Project project, SequenceId? sequenceId, ExportRange range, string outputPath,
        Func<string, IAudioEffect?>? effectFactory = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        Sequence sequence = sequenceId is { } id
            ? project.GetSequence(id) ?? throw new ArgumentException($"No sequence with id {id} in the project.", nameof(sequenceId))
            : project.ActiveSequence;

        Timeline timeline = sequence.Timeline;
        int sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 48000;
        const int channels = 2; // the preview output format (MediaBootstrap wires the same)

        ExportRange effective = range.ClampTo(timeline.Duration);
        long totalFrames = effective.Duration.ToSampleIndex(sampleRate);
        if (totalFrames <= 0)
            throw new ArgumentException("The render range is empty — nothing to render.", nameof(range));

        bool completed = false;
        try
        {
            using var mixer = new AudioMixer(
                sampleRate, channels,
                mediaId => VideoExporter.OpenPcmReader(project, mediaId, sampleRate, channels),
                effectFactory);
            using var writer = new WavePcmWriter(outputPath, sampleRate, channels);

            const int chunkFrames = 2048;
            float[] buffer = new float[chunkFrames * channels];
            long next = 0;
            while (next < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(chunkFrames, totalFrames - next);
                Span<float> span = buffer.AsSpan(0, chunk * channels);
                mixer.MixInto(span, effective.In + Timecode.FromSamples(next, sampleRate), project, sequence);
                writer.Write(span);
                next += chunk;
                progress?.Report((double)next / totalFrames);
            }

            writer.Finish();
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // Best-effort cleanup: an unfinished WAV carries zero sizes and would be rejected by the reader
                // anyway, but don't leave dead bytes in the cache dir.
                try { if (File.Exists(outputPath)) File.Delete(outputPath); }
                catch { /* never mask the original failure */ }
            }
        }
    }
}
