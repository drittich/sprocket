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
/// <remarks>
/// A nested-sequence clip (PLAN.md step 23) resolves <b>recursively</b>: the planner descends into the child
/// sequence at the mapped time, producing a nested plan carried on the layer — "the graph already turns a
/// (timeline, t) into a frame" (ARCHITECTURE.md §5/§17). The recursion tracks the sequences on the current path
/// so a cycle (a sequence containing itself, directly or transitively) contributes nothing instead of looping
/// forever, and stops at <see cref="SequenceGraph.MaxNestingDepth"/>.
/// </remarks>
public static class RenderGraph
{
    /// <summary>
    /// Resolves the composited-frame plan for the project's <see cref="Project.ActiveSequence"/> at timeline
    /// time <paramref name="t"/>.
    /// </summary>
    public static VideoFramePlan PlanVideoFrame(Project project, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(project);
        return PlanVideoFrame(project, project.ActiveSequence, t);
    }

    /// <summary>
    /// Resolves the composited-frame plan for <paramref name="sequence"/> at timeline time <paramref name="t"/>:
    /// for each enabled video track, bottom→top, find the active clip, map the time into its source, and evaluate
    /// its effect stack. A nested-sequence clip resolves its child recursively (PLAN.md step 23). Tracks with no
    /// active clip contribute no layer.
    /// </summary>
    public static VideoFramePlan PlanVideoFrame(Project project, Sequence sequence, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        return PlanVideoFrameCore(project, sequence, t, [sequence.Id], depth: 0);
    }

    private static VideoFramePlan PlanVideoFrameCore(
        Project project, Sequence sequence, Timecode t, HashSet<SequenceId> path, int depth)
    {
        var layers = new List<VideoLayer>();
        foreach (VideoTrack track in sequence.Timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;

            Clip? clip = track.ResolveActiveClip(t);
            if (clip is null)
                continue;

            Timecode sourceT = clip.MapToSource(t);
            IReadOnlyList<ResolvedEffect> effects = ResolveEffectsCore(clip, t);
            switch (clip.Kind)
            {
                case ClipKind.Generator:
                    layers.Add(new VideoLayer(
                        default, sourceT, effects, track.Opacity, track.BlendMode,
                        LayerKind.Generator, ResolveGeneratorCore(clip.Generator!, t)));
                    break;

                case ClipKind.Adjustment:
                    layers.Add(new VideoLayer(
                        default, sourceT, effects, track.Opacity, track.BlendMode, LayerKind.Adjustment));
                    break;

                case ClipKind.Sequence:
                    // A valid, in-bounds, non-cyclic nested sequence carries its resolved child plan; a missing /
                    // cyclic / too-deep reference contributes nothing (renders as empty, like an offline source §15).
                    if (PlanNestedVideo(project, clip, sourceT, path, depth) is { } nested)
                        layers.Add(new VideoLayer(
                            default, sourceT, effects, track.Opacity, track.BlendMode,
                            LayerKind.Sequence, NestedPlan: nested));
                    break;

                case ClipKind.Multicam:
                    // The active angle resolves to an ordinary media frame at the synced source time (PLAN.md
                    // step 24) — so multicam rides the media seam, no recursion needed. A missing source or a
                    // stale angle index contributes nothing (renders as empty, §15).
                    if (ResolveMulticamAngle(project, clip) is { } angle)
                        layers.Add(new VideoLayer(
                            angle.MediaRefId, ClipSync.AngleSourceTime(angle, sourceT),
                            effects, track.Opacity, track.BlendMode));
                    break;

                default:
                    layers.Add(new VideoLayer(clip.MediaRefId, sourceT, effects, track.Opacity, track.BlendMode));
                    break;
            }
        }

        return new VideoFramePlan(sequence.Timeline.Resolution, t, layers);
    }

    /// <summary>Resolves the child plan for a nested-sequence clip at the clip-local time <paramref name="childTime"/>,
    /// or <see langword="null"/> when the reference is missing, would exceed the depth guard, or would form a cycle.</summary>
    private static VideoFramePlan? PlanNestedVideo(
        Project project, Clip clip, Timecode childTime, HashSet<SequenceId> path, int depth)
    {
        if (clip.SourceSequenceId is not { } childId || depth + 1 > SequenceGraph.MaxNestingDepth)
            return null;
        if (project.GetSequence(childId) is not { } child)
            return null;
        if (!path.Add(childId))
            return null; // childId already on the recursion path → cycle

        try
        {
            return PlanVideoFrameCore(project, child, childTime, path, depth + 1);
        }
        finally
        {
            path.Remove(childId); // leave the path as we found it so sibling nests of the same child still resolve
        }
    }

    /// <summary>
    /// Resolves the audio buffer plan for the project's <see cref="Project.ActiveSequence"/> over the half-open
    /// range <c>[<paramref name="bufferStart"/>, bufferStart + <paramref name="bufferDuration"/>)</c>: for each
    /// audible audio track (mute/solo honoured), find the active clip, map to source, and compute the linear gain
    /// at both ends of the buffer (track gain × fade envelope) so the mixer can ramp across it. A nested-sequence
    /// clip resolves its child's audio recursively (PLAN.md step 23).
    /// </summary>
    public static AudioBufferPlan PlanAudioBuffer(Project project, Timecode bufferStart, Timecode bufferDuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (bufferDuration.Ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferDuration), "Buffer duration must be non-negative.");

        // The project master gain is applied once, at the root; nested sub-mixes carry unity master gain.
        return PlanAudioBufferCore(
            project, project.ActiveSequence, bufferStart, bufferDuration,
            DbToLinear(project.Settings.MasterGainDb), [project.ActiveSequence.Id], depth: 0);
    }

    private static AudioBufferPlan PlanAudioBufferCore(
        Project project, Sequence sequence, Timecode bufferStart, Timecode bufferDuration,
        double masterGainLinear, HashSet<SequenceId> path, int depth)
    {
        Timecode bufferEnd = bufferStart + bufferDuration;
        bool anySolo = sequence.Timeline.AudioTracks.Any(at => at is { Enabled: true, Solo: true });

        var layers = new List<AudioLayer>();
        foreach (AudioTrack track in sequence.Timeline.AudioTracks)
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

            if (clip.Kind == ClipKind.Sequence)
            {
                // A nested sequence's audio is a sub-mix the nesting clip's gain/fade applies over. (Retiming a
                // nested clip's audio is deferred — the child sub-mix plays at 1×; see PLAN.md step 23.)
                AudioBufferPlan? nested = PlanNestedAudio(project, clip, bufferStart, bufferDuration, path, depth);
                if (nested is not null)
                    layers.Add(new AudioLayer(default, clip.MapToSource(bufferStart), gainStart, gainEnd, clip.SpeedRatio, nested));
            }
            else if (clip.Kind == ClipKind.Multicam)
            {
                // The active angle's audio is an ordinary source pulled at the synced time (PLAN.md step 24); a
                // missing source / stale angle index contributes nothing (§15).
                if (ResolveMulticamAngle(project, clip) is { } angle)
                    layers.Add(new AudioLayer(
                        angle.EffectiveAudioRefId, ClipSync.AngleSourceTime(angle, clip.MapToSource(bufferStart)),
                        gainStart, gainEnd, clip.SpeedRatio));
            }
            else
            {
                layers.Add(new AudioLayer(clip.MediaRefId, clip.MapToSource(bufferStart), gainStart, gainEnd, clip.SpeedRatio));
            }
        }

        return new AudioBufferPlan(bufferStart, bufferDuration, layers, masterGainLinear);
    }

    private static AudioBufferPlan? PlanNestedAudio(
        Project project, Clip clip, Timecode bufferStart, Timecode bufferDuration, HashSet<SequenceId> path, int depth)
    {
        if (clip.SourceSequenceId is not { } childId || depth + 1 > SequenceGraph.MaxNestingDepth)
            return null;
        if (project.GetSequence(childId) is not { } child)
            return null;
        if (!path.Add(childId))
            return null;

        try
        {
            // Map the buffer's start into the child's timeline; the sub-mix is rendered at unity master gain.
            return PlanAudioBufferCore(
                project, child, clip.MapToSource(bufferStart), bufferDuration, 1.0, path, depth + 1);
        }
        finally
        {
            path.Remove(childId);
        }
    }

    /// <summary>Resolves the active angle of a multicam clip — the angle at <see cref="Clip.ActiveAngle"/> in the
    /// referenced <see cref="MulticamSource"/>, or <see langword="null"/> when the source is missing or the angle
    /// index is out of range (renders as nothing, §15).</summary>
    private static MulticamAngle? ResolveMulticamAngle(Project project, Clip clip)
    {
        if (clip.SourceMulticamId is not { } id || project.GetMulticam(id) is not { } source)
            return null;
        return source.AngleAt(clip.ActiveAngle);
    }

    /// <summary>
    /// Executes a video plan against the layer seams: create a transparent surface, then for each layer fetch its
    /// content, fold its effect chain, and composite it on. Returns the snapshotted composited frame. This is the
    /// single code path shared by preview and export. Generator layers draw via
    /// <see cref="IVideoCompositor{TImage}.CreateGeneratorFrame"/>; an adjustment layer applies its effects to a
    /// snapshot of the composite so far; a nested-sequence layer is rendered <b>recursively</b> from its
    /// <see cref="VideoLayer.NestedPlan"/> (ARCHITECTURE.md §5, PLAN.md steps 19/23).
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
            // layer's effects over that, and blend the graded result back with the layer's opacity/blend. A nested
            // sequence renders its child plan recursively into its own frame, then composites like any layer.
            TImage frame = layer.Kind switch
            {
                LayerKind.Generator => compositor.CreateGeneratorFrame(layer.Generator!, plan.Resolution, layer.SourceTime),
                LayerKind.Adjustment => compositor.Snapshot(surface),
                LayerKind.Sequence => Render(layer.NestedPlan!, frameSource, compositor),
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
