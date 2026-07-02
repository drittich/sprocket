using Sprocket.Core.Audio;
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

            // A transition active at t blends the two adjacent clips (PLAN.md step 25). When it resolves to a valid
            // pair it replaces the single-clip layer for this track; an invalid one (no real cut, or a side that
            // resolves to nothing) falls through to ordinary single-clip resolution.
            if (track.ResolveTransitionAt(t) is { } transition
                && ResolveTransitionLayer(project, track, transition, t, path, depth) is { } transitionLayer)
            {
                layers.Add(transitionLayer);
                continue;
            }

            Clip? clip = track.ResolveActiveClip(t);
            if (clip is null)
                continue;

            if (ResolveClipLayer(project, clip, t, track.Opacity, track.BlendMode, path, depth) is { } layer)
                layers.Add(layer);
        }

        return new VideoFramePlan(sequence.Timeline.Resolution, t, layers);
    }

    /// <summary>
    /// Resolves a single clip into a <see cref="VideoLayer"/> with the given compositing <paramref name="opacity"/>/
    /// <paramref name="blend"/>, or <see langword="null"/> when the clip resolves to nothing (a missing/cyclic nested
    /// sequence or a stale multicam angle — renders as empty, §15). This is the shared per-clip resolution used both
    /// for an ordinary track layer (with the track's opacity/blend) and for each side of a transition (at unity
    /// opacity / normal blend, since the transition layer carries the track's compositing).
    /// </summary>
    private static VideoLayer? ResolveClipLayer(
        Project project, Clip clip, Timecode t, double opacity, BlendMode blend, HashSet<SequenceId> path, int depth)
    {
        Timecode sourceT = clip.MapToSource(t);
        IReadOnlyList<ResolvedEffect> effects = ResolveEffectsCore(clip, t);
        switch (clip.Kind)
        {
            case ClipKind.Generator:
                return new VideoLayer(
                    default, sourceT, effects, opacity, blend,
                    LayerKind.Generator, ResolveGeneratorCore(clip.Generator!, t));

            case ClipKind.Adjustment:
                return new VideoLayer(default, sourceT, effects, opacity, blend, LayerKind.Adjustment);

            case ClipKind.Sequence:
                // A valid, in-bounds, non-cyclic nested sequence carries its resolved child plan; a missing /
                // cyclic / too-deep reference contributes nothing (renders as empty, like an offline source §15).
                return PlanNestedVideo(project, clip, sourceT, path, depth) is { } nested
                    ? new VideoLayer(default, sourceT, effects, opacity, blend, LayerKind.Sequence, NestedPlan: nested)
                    : null;

            case ClipKind.Multicam:
                // The active angle resolves to an ordinary media frame at the synced source time (PLAN.md step 24)
                // — so multicam rides the media seam, no recursion needed. A missing source or a stale angle index
                // contributes nothing (renders as empty, §15).
                return ResolveMulticamAngle(project, clip) is { } angle
                    ? new VideoLayer(angle.MediaRefId, ClipSync.AngleSourceTime(angle, sourceT), effects, opacity, blend)
                    : null;

            default:
                return new VideoLayer(clip.MediaRefId, sourceT, effects, opacity, blend);
        }
    }

    /// <summary>
    /// Resolves a transition active at <paramref name="t"/> into a <see cref="LayerKind.Transition"/> layer blending
    /// its two clips (PLAN.md step 25), or <see langword="null"/> when it is not a real cut between two distinct
    /// resolvable clips (then the caller renders the track's single active clip instead). Each side is resolved at
    /// the same time <paramref name="t"/> — the outgoing clip is sampled into its handles past the cut, the incoming
    /// clip into its handles before the cut — exactly as an NLE does.
    /// </summary>
    private static VideoLayer? ResolveTransitionLayer(
        Project project, VideoTrack track, Transition transition, Timecode t, HashSet<SequenceId> path, int depth)
    {
        (Clip? fromClip, Clip? toClip) = track.ResolveTransitionClips(transition);
        if (fromClip is null || toClip is null || ReferenceEquals(fromClip, toClip))
            return null; // not a real cut between two clips

        VideoLayer? from = ResolveClipLayer(project, fromClip, t, 1.0, BlendMode.Normal, path, depth);
        VideoLayer? to = ResolveClipLayer(project, toClip, t, 1.0, BlendMode.Normal, path, depth);
        if (from is null || to is null)
            return null; // a side resolved to nothing — render normally rather than blending against emptiness

        IReadOnlyDictionary<string, double> values = ResolveTransitionParams(transition, t);
        var resolved = new ResolvedTransition(transition.TransitionTypeId, transition.ProgressAt(t), values, from, to);
        return new VideoLayer(default, default, [], track.Opacity, track.BlendMode, LayerKind.Transition, Transition: resolved);
    }

    private static readonly Dictionary<string, double> NoTransitionParams = new();

    private static IReadOnlyDictionary<string, double> ResolveTransitionParams(Transition transition, Timecode t)
    {
        if (transition.Parameters.Count == 0)
            return NoTransitionParams;
        var values = new Dictionary<string, double>(transition.Parameters.Count);
        foreach ((string name, AnimatableValue value) in transition.Parameters)
            values[name] = value.Evaluate(t);
        return values;
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
        return PlanAudioBuffer(project, project.ActiveSequence, bufferStart, bufferDuration);
    }

    /// <summary>
    /// Resolves the audio buffer plan for <paramref name="sequence"/> over the half-open range
    /// <c>[<paramref name="bufferStart"/>, bufferStart + <paramref name="bufferDuration"/>)</c>. Same resolution as
    /// the active-sequence overload but for an arbitrary sequence — export can render any sequence, not just the one
    /// open for editing (PLAN.md step 29 export queue). The project master gain still applies once at the root.
    /// </summary>
    public static AudioBufferPlan PlanAudioBuffer(
        Project project, Sequence sequence, Timecode bufferStart, Timecode bufferDuration, AudioPlanScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (bufferDuration.Ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferDuration), "Buffer duration must be non-negative.");

        // The project master gain is applied once, at the root; nested sub-mixes carry unity master gain.
        double masterGain = scope?.UnityMasterGain == true ? 1.0 : DbToLinear(project.Settings.MasterGainDb);
        return PlanAudioBufferCore(
            project, sequence, bufferStart, bufferDuration,
            masterGain, [sequence.Id], depth: 0, scope);
    }

    private static AudioBufferPlan PlanAudioBufferCore(
        Project project, Sequence sequence, Timecode bufferStart, Timecode bufferDuration,
        double masterGainLinear, HashSet<SequenceId> path, int depth, AudioPlanScope? scope = null,
        object? busStateKey = null)
    {
        Timecode bufferEnd = bufferStart + bufferDuration;
        bool anySolo = sequence.Timeline.AudioTracks.Any(at => at is { Enabled: true, Solo: true });

        // Scope applies only at the top level of the measured sequence; nested sub-mixes always use full gains.
        AudioPlanScope? active = depth == 0 ? scope : null;

        var layers = new List<AudioLayer>();
        foreach (AudioTrack track in sequence.Timeline.AudioTracks)
        {
            if (active?.OnlyTrack is { } only)
            {
                // Measuring one track: include only it, ignoring its own mute/solo/enabled so its content is
                // measurable regardless of the current mix state.
                if (!ReferenceEquals(track, only))
                    continue;
            }
            else
            {
                if (!track.Enabled || track.Muted)
                    continue;
                if (anySolo && !track.Solo)
                    continue;
            }

            Clip? clip = track.ResolveActiveClip(bufferStart);
            if (clip is null)
                continue;

            // The clip-level gain (clip gain × fade, ramped) and the track fader stay split so the mixer can run
            // the track's insert chain between them — the standard pre-fader insert point (PLAN.md step 31).
            double trackGain = active?.UnityTrackGain == true ? 1.0 : DbToLinear(track.GainDb);
            double clipGain = DbToLinear(clip.GainDb);
            double clipGainStart = clipGain * FadeGain(clip, bufferStart);
            double clipGainEnd = clipGain * FadeGain(clip, bufferEnd);
            (double panL, double panR) = PanLaw.Balance(track.Pan);
            ResolvedAudioChain? clipChain = ResolveAudioChain(clip.Effects, clip, bufferStart);
            ResolvedAudioChain? trackChain = ResolveAudioChain(track.Effects, track, bufferStart);

            if (clip.Kind == ClipKind.Sequence)
            {
                // A nested sequence's audio is a sub-mix the nesting clip's gain/fade applies over. (Retiming a
                // nested clip's audio is deferred — the child sub-mix plays at 1×; see PLAN.md step 23.)
                AudioBufferPlan? nested = PlanNestedAudio(project, clip, bufferStart, bufferDuration, path, depth);
                if (nested is not null)
                    layers.Add(new AudioLayer(default, clip.MapToSource(bufferStart), clipGainStart, clipGainEnd,
                        trackGain, clip.SpeedRatio, nested, panL, panR, clipChain, trackChain));
            }
            else if (clip.Kind == ClipKind.Multicam)
            {
                // The active angle's audio is an ordinary source pulled at the synced time (PLAN.md step 24); a
                // missing source / stale angle index contributes nothing (§15).
                if (ResolveMulticamAngle(project, clip) is { } angle)
                    layers.Add(new AudioLayer(
                        angle.EffectiveAudioRefId, ClipSync.AngleSourceTime(angle, clip.MapToSource(bufferStart)),
                        clipGainStart, clipGainEnd, trackGain, clip.SpeedRatio,
                        PanLeft: panL, PanRight: panR, ClipChain: clipChain, TrackChain: trackChain));
            }
            else
            {
                layers.Add(new AudioLayer(clip.MediaRefId, clip.MapToSource(bufferStart), clipGainStart, clipGainEnd,
                    trackGain, clip.SpeedRatio, PanLeft: panL, PanRight: panR, ClipChain: clipChain, TrackChain: trackChain));
            }
        }

        // Output chains (PLAN.md step 31): the sequence's bus chain, then — at the root only — the project master
        // chain, both pre-master-fader. A UnityMasterGain measurement scope bypasses the master chain too (it
        // measures "before the master"). Nested sub-plans key their bus chain by the nesting clip so two clips
        // nesting the same child sequence get independent DSP state.
        List<ResolvedAudioChain>? outputChains = null;
        if (ResolveAudioChain(sequence.Timeline.AudioEffects, busStateKey ?? sequence.Timeline, bufferStart) is { } bus)
            (outputChains ??= []).Add(bus);
        if (depth == 0 && scope?.UnityMasterGain != true &&
            ResolveAudioChain(project.Settings.MasterAudioEffects, project.Settings, bufferStart) is { } master)
            (outputChains ??= []).Add(master);

        return new AudioBufferPlan(bufferStart, bufferDuration, layers, masterGainLinear, outputChains);
    }

    /// <summary>
    /// Resolves the <b>audio</b> effects in <paramref name="effects"/> (ids per <see cref="EffectTypeIds.IsAudio"/>,
    /// preserving order, skipping video effects) at time <paramref name="t"/> into a chain keyed by
    /// <paramref name="stateKey"/>, or <see langword="null"/> when there are none — the common fast path
    /// (PLAN.md step 31). Fades are not chain stages: they stay the gain envelope (<see cref="FadeGain"/>).
    /// </summary>
    private static ResolvedAudioChain? ResolveAudioChain(List<EffectInstance> effects, object stateKey, Timecode t)
    {
        List<ResolvedEffect>? resolved = null;
        foreach (EffectInstance effect in effects)
        {
            if (!effect.Enabled || !EffectTypeIds.IsAudio(effect.EffectTypeId))
                continue;
            var values = new Dictionary<string, double>(effect.Parameters.Count);
            foreach ((string name, AnimatableValue value) in effect.Parameters)
                values[name] = value.Evaluate(t);
            (resolved ??= []).Add(new ResolvedEffect(effect.EffectTypeId, values));
        }
        return resolved is null ? null : new ResolvedAudioChain(stateKey, resolved);
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
            // The child's bus chain is keyed by the nesting clip so two clips nesting the same sequence keep
            // independent DSP state (PLAN.md step 31).
            return PlanAudioBufferCore(
                project, child, clip.MapToSource(bufferStart), bufferDuration, 1.0, path, depth + 1,
                busStateKey: clip);
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

        // Produces a single (non-transition) layer's frame: its content folded through its own effect chain.
        // An adjustment layer has no content of its own — it takes the composite beneath it (Snapshot(surface)).
        TImage RenderLayerFrame(VideoLayer l)
        {
            TImage f = l.Kind switch
            {
                LayerKind.Generator => compositor.CreateGeneratorFrame(l.Generator!, plan.Resolution, l.SourceTime),
                LayerKind.Adjustment => compositor.Snapshot(surface),
                LayerKind.Sequence => Render(l.NestedPlan!, frameSource, compositor),
                _ => frameSource.GetFrame(l.MediaRefId, l.SourceTime),
            };
            foreach (ResolvedEffect effect in l.Effects)
                f = compositor.ApplyEffect(f, effect);
            return f;
        }

        foreach (VideoLayer layer in plan.Layers)
        {
            // A transition layer blends two clips' (effect-folded) frames per the transition, then composites the
            // result like any layer (PLAN.md step 25). Every other layer renders its own content + effect chain; a
            // nested sequence renders its child plan recursively into its own frame, then composites.
            TImage frame;
            if (layer.Kind == LayerKind.Transition)
            {
                ResolvedTransition tr = layer.Transition!;
                frame = compositor.ApplyTransition(RenderLayerFrame(tr.From), RenderLayerFrame(tr.To), tr);
            }
            else
            {
                frame = RenderLayerFrame(layer);
            }

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

        List<ResolvedEffect>? resolved = null;
        foreach (EffectInstance effect in clip.Effects)
        {
            if (!effect.Enabled)
                continue;
            var values = new Dictionary<string, double>(effect.Parameters.Count);
            foreach ((string name, AnimatableValue value) in effect.Parameters)
                values[name] = value.Evaluate(t);
            (resolved ??= []).Add(new ResolvedEffect(effect.EffectTypeId, values));
        }
        return resolved?.ToArray() ?? [];
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
            if (effect.Enabled && effect.EffectTypeId == EffectTypeIds.Fade &&
                effect.Parameters.TryGetValue(EffectParamNames.Opacity, out AnimatableValue? opacity))
            {
                gain *= opacity.Evaluate(t);
            }
        }
        return gain;
    }

    private static double DbToLinear(double db) => Math.Pow(10, db / 20.0);
}
