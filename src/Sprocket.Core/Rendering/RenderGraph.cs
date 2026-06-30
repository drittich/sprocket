using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Rendering;

/// <summary>
/// The heart of the editor (ARCHITECTURE.md §5). Pull-based and stateless: given a project and a time
/// it produces a pure <see cref="VideoFramePlan"/>/<see cref="AudioBufferPlan"/> (the <em>resolution</em>
/// step), and <see cref="Render{TImage}"/> executes a video plan against the layer seams. Because the
/// resolution is pure data it is reused identically for preview and export and is trivial to unit-test
/// headlessly — no Skia, no FFmpeg, no GPU.
/// </summary>
public static class RenderGraph
{
    /// <summary>
    /// Resolves the composited-frame plan at timeline time <paramref name="t"/>: for each enabled
    /// video track, bottom→top, find the active clip, map the time into its source, and evaluate its
    /// effect stack. Tracks with no active clip contribute no layer.
    /// </summary>
    public static VideoFramePlan PlanVideoFrame(Project project, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(project);

        var layers = new List<VideoLayer>();
        foreach (VideoTrack track in project.Timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;

            Clip? clip = track.ResolveActiveClip(t);
            if (clip is null)
                continue;

            Timecode sourceT = clip.MapToSource(t);
            IReadOnlyList<ResolvedEffect> effects = ResolveEffectsCore(clip, t);
            layers.Add(clip.Kind switch
            {
                ClipKind.Generator => new VideoLayer(
                    default, sourceT, effects, track.Opacity, track.BlendMode,
                    LayerKind.Generator, ResolveGeneratorCore(clip.Generator!, t)),
                ClipKind.Adjustment => new VideoLayer(
                    default, sourceT, effects, track.Opacity, track.BlendMode, LayerKind.Adjustment),
                _ => new VideoLayer(clip.MediaRefId, sourceT, effects, track.Opacity, track.BlendMode),
            });
        }

        return new VideoFramePlan(project.Timeline.Resolution, t, layers);
    }

    /// <summary>
    /// Resolves the audio buffer plan for the half-open range
    /// <c>[<paramref name="bufferStart"/>, bufferStart + <paramref name="bufferDuration"/>)</c>:
    /// for each audible audio track (mute/solo honoured), find the active clip, map to source, and
    /// compute the linear gain at both ends of the buffer (track gain × fade envelope) so the mixer
    /// can ramp across it.
    /// </summary>
    public static AudioBufferPlan PlanAudioBuffer(Project project, Timecode bufferStart, Timecode bufferDuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (bufferDuration.Ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferDuration), "Buffer duration must be non-negative.");

        Timecode bufferEnd = bufferStart + bufferDuration;
        bool anySolo = project.Timeline.AudioTracks.Any(at => at is { Enabled: true, Solo: true });

        var layers = new List<AudioLayer>();
        foreach (AudioTrack track in project.Timeline.AudioTracks)
        {
            if (!track.Enabled || track.Muted)
                continue;
            if (anySolo && !track.Solo)
                continue;

            Clip? clip = track.ResolveActiveClip(bufferStart);
            if (clip is null)
                continue;

            double trackGain = DbToLinear(track.GainDb);
            double gainStart = trackGain * FadeGain(clip, bufferStart);
            double gainEnd = trackGain * FadeGain(clip, bufferEnd);
            layers.Add(new AudioLayer(clip.MediaRefId, clip.MapToSource(bufferStart), gainStart, gainEnd, clip.SpeedRatio));
        }

        return new AudioBufferPlan(bufferStart, bufferDuration, layers, DbToLinear(project.Settings.MasterGainDb));
    }

    /// <summary>
    /// Executes a video plan against the layer seams: create a transparent surface, then for each layer fetch its
    /// content, fold its effect chain, and composite it on. Returns the snapshotted composited frame. This is the
    /// single code path shared by preview and export. Generator layers draw via
    /// <see cref="IVideoCompositor{TImage}.CreateGeneratorFrame"/>; an adjustment layer applies its effects to a
    /// snapshot of the composite so far and blends the result back (ARCHITECTURE.md §5, PLAN.md step 19).
    /// </summary>
    public static TImage Render<TImage>(
        VideoFramePlan plan,
        IFrameSource<TImage> frameSource,
        IVideoCompositor<TImage> compositor)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentNullException.ThrowIfNull(compositor);

        TImage surface = compositor.CreateTransparentSurface(plan.Resolution);
        foreach (VideoLayer layer in plan.Layers)
        {
            // An adjustment layer has no content of its own: take what is already composited beneath it, run the
            // layer's effects over that, and blend the graded result back with the layer's opacity/blend.
            TImage frame = layer.Kind switch
            {
                LayerKind.Generator => compositor.CreateGeneratorFrame(layer.Generator!, plan.Resolution, layer.SourceTime),
                LayerKind.Adjustment => compositor.Snapshot(surface),
                _ => frameSource.GetFrame(layer.MediaRefId, layer.SourceTime),
            };

            foreach (ResolvedEffect effect in layer.Effects)
                frame = compositor.ApplyEffect(frame, effect);

            compositor.Composite(surface, frame, layer.Opacity, layer.BlendMode);
        }

        return compositor.Snapshot(surface);
    }

    /// <summary>
    /// Evaluates a clip's effect stack at timeline time <paramref name="t"/> into the order-preserving
    /// <see cref="ResolvedEffect"/> list the Render layer turns into shaders. Exposed so the playback
    /// preview can resolve effects for the live frame off the same code path the planner uses (§5, §7).
    /// Returns an empty list for a clip with no effects (no allocation).
    /// </summary>
    public static IReadOnlyList<ResolvedEffect> ResolveEffects(Clip clip, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return ResolveEffectsCore(clip, t);
    }

    /// <summary>
    /// Evaluates a generator's animatable parameters at timeline time <paramref name="t"/> into a
    /// <see cref="ResolvedGenerator"/> the Render layer draws from (PLAN.md step 19). String parameters pass
    /// through unchanged. Exposed so the playback preview can resolve a generator layer off the same path the
    /// planner uses.
    /// </summary>
    public static ResolvedGenerator ResolveGenerator(GeneratorSpec generator, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return ResolveGeneratorCore(generator, t);
    }

    private static ResolvedGenerator ResolveGeneratorCore(GeneratorSpec generator, Timecode t)
    {
        var values = new Dictionary<string, double>(generator.Parameters.Count);
        foreach ((string name, AnimatableValue value) in generator.Parameters)
            values[name] = value.Evaluate(t);
        // Copy the string map so the resolved generator is an immutable snapshot independent of later edits.
        var strings = new Dictionary<string, string>(generator.Strings);
        return new ResolvedGenerator(generator.GeneratorTypeId, strings, values);
    }

    private static ResolvedEffect[] ResolveEffectsCore(Clip clip, Timecode t)
    {
        if (clip.Effects.Count == 0)
            return [];

        var resolved = new ResolvedEffect[clip.Effects.Count];
        for (int i = 0; i < clip.Effects.Count; i++)
        {
            EffectInstance effect = clip.Effects[i];
            var values = new Dictionary<string, double>(effect.Parameters.Count);
            foreach ((string name, AnimatableValue value) in effect.Parameters)
                values[name] = value.Evaluate(t);
            resolved[i] = new ResolvedEffect(effect.EffectTypeId, values);
        }
        return resolved;
    }

    /// <summary>
    /// The combined fade multiplier for a clip at time <paramref name="t"/> — the product of every
    /// <see cref="EffectTypeIds.Fade"/> effect's evaluated opacity. This same value drives video alpha
    /// (in the shader) and audio gain (here, in the mixer plan), so a fade affects both consistently.
    /// </summary>
    private static double FadeGain(Clip clip, Timecode t)
    {
        double gain = 1.0;
        foreach (EffectInstance effect in clip.Effects)
        {
            if (effect.EffectTypeId == EffectTypeIds.Fade &&
                effect.Parameters.TryGetValue(EffectParamNames.Opacity, out AnimatableValue? opacity))
            {
                gain *= opacity.Evaluate(t);
            }
        }
        return gain;
    }

    private static double DbToLinear(double db) => Math.Pow(10, db / 20.0);
}
