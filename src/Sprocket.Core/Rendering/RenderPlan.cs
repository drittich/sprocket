using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Rendering;

/// <summary>
/// An effect with its parameters already evaluated to concrete numbers at a specific time. The Render
/// layer turns this into a shader; Core never sees the shader (ARCHITECTURE.md §5, §7).
/// </summary>
/// <param name="EffectTypeId">The effect type, e.g. <see cref="EffectTypeIds.Brightness"/>.</param>
/// <param name="Parameters">Parameter values, evaluated at the frame's time.</param>
public sealed record ResolvedEffect(string EffectTypeId, IReadOnlyDictionary<string, double> Parameters)
{
    /// <summary>Gets a parameter value, or <paramref name="fallback"/> if it is not set.</summary>
    public double Get(string name, double fallback = 0) =>
        Parameters.TryGetValue(name, out double value) ? value : fallback;
}

/// <summary>
/// A generator's parameters already evaluated to concrete values at a specific time (PLAN.md step 19). The
/// Render layer turns this into drawn pixels; Core never draws.
/// </summary>
/// <param name="GeneratorTypeId">The generator type, e.g. <see cref="GeneratorTypeIds.Title"/>.</param>
/// <param name="Strings">String parameters (text, colour hex).</param>
/// <param name="Parameters">Numeric parameters, evaluated at the frame's time.</param>
public sealed record ResolvedGenerator(
    string GeneratorTypeId,
    IReadOnlyDictionary<string, string> Strings,
    IReadOnlyDictionary<string, double> Parameters)
{
    /// <summary>Gets a numeric parameter, or <paramref name="fallback"/> if it is not set.</summary>
    public double Get(string name, double fallback = 0) =>
        Parameters.TryGetValue(name, out double value) ? value : fallback;

    /// <summary>Gets a string parameter, or <paramref name="fallback"/> if it is not set.</summary>
    public string GetString(string name, string fallback = "") =>
        Strings.TryGetValue(name, out string? value) ? value : fallback;
}

/// <summary>What produces a <see cref="VideoLayer"/>'s pixels (PLAN.md step 19, step 23, step 25).</summary>
public enum LayerKind
{
    /// <summary>Decoded source media fetched via <see cref="IFrameSource{TImage}"/>.</summary>
    Media,

    /// <summary>Drawn procedurally from a <see cref="VideoLayer.Generator"/>.</summary>
    Generator,

    /// <summary>An adjustment layer: its effects apply to the composite of the layers already drawn beneath it.</summary>
    Adjustment,

    /// <summary>A nested sequence (PLAN.md step 23): its pixels come from rendering <see cref="VideoLayer.NestedPlan"/>
    /// (the child sequence's resolved plan at the mapped time) and compositing the result like any other layer.</summary>
    Sequence,

    /// <summary>A transition (PLAN.md step 25): its pixels come from blending two clips' frames — the outgoing and
    /// incoming layers in <see cref="VideoLayer.Transition"/> — per the transition type and progress, then
    /// compositing the blended result like any other layer.</summary>
    Transition,
}

/// <summary>
/// One resolved video layer: how to produce its pixels (<see cref="Kind"/>), the effect chain to apply
/// (bottom→top), and how to composite it onto the layers beneath. For a <see cref="LayerKind.Media"/> layer
/// <see cref="MediaRefId"/>/<see cref="SourceTime"/> name the source frame; for a <see cref="LayerKind.Generator"/>
/// layer <see cref="Generator"/> describes the procedural content (and <see cref="SourceTime"/> is its local time);
/// a <see cref="LayerKind.Adjustment"/> layer has no content and applies its effects to what is already composited.
/// </summary>
/// <param name="MediaRefId">Source to fetch from (media layers).</param>
/// <param name="SourceTime">Time within the source / generator-local time.</param>
/// <param name="Effects">Effect chain, evaluated at the frame's time, applied in order.</param>
/// <param name="Opacity">Track opacity for the composite step.</param>
/// <param name="BlendMode">Track blend mode for the composite step.</param>
/// <param name="Kind">What produces this layer's pixels.</param>
/// <param name="Generator">The procedural source (generator layers only).</param>
/// <param name="NestedPlan">The child sequence's resolved frame plan (<see cref="LayerKind.Sequence"/> layers only,
/// PLAN.md step 23): render it, then apply this layer's effect chain and composite it like any other layer.</param>
/// <param name="Transition">The two clips to blend and how (<see cref="LayerKind.Transition"/> layers only,
/// PLAN.md step 25): produce each side's frame, blend them per the transition, then composite the result with this
/// layer's <see cref="Opacity"/>/<see cref="BlendMode"/> (the track's). The transition layer's own
/// <see cref="MediaRefId"/>/<see cref="SourceTime"/>/<see cref="Effects"/> are unused.</param>
public sealed record VideoLayer(
    MediaRefId MediaRefId,
    Timecode SourceTime,
    IReadOnlyList<ResolvedEffect> Effects,
    double Opacity,
    BlendMode BlendMode,
    LayerKind Kind = LayerKind.Media,
    ResolvedGenerator? Generator = null,
    VideoFramePlan? NestedPlan = null,
    ResolvedTransition? Transition = null);

/// <summary>
/// A transition resolved at a frame's time (PLAN.md step 25): which two clips to blend (<see cref="From"/> outgoing,
/// <see cref="To"/> incoming — each a fully-resolved <see cref="VideoLayer"/> with its own clip effects, at unity
/// opacity / normal blend), the blend <see cref="Progress"/> in [0, 1], and any evaluated type parameters. The
/// Render layer turns this into a two-input shader; Core never sees the shader (§5, §7).
/// </summary>
/// <param name="TransitionTypeId">The transition type, e.g. <see cref="TransitionTypeIds.CrossDissolve"/>.</param>
/// <param name="Progress">0 = full outgoing clip, 1 = full incoming clip.</param>
/// <param name="Parameters">Type parameters, evaluated at the frame's time (empty for the v1 built-ins).</param>
/// <param name="From">The outgoing clip's resolved layer (shown at progress 0).</param>
/// <param name="To">The incoming clip's resolved layer (shown at progress 1).</param>
public sealed record ResolvedTransition(
    string TransitionTypeId,
    double Progress,
    IReadOnlyDictionary<string, double> Parameters,
    VideoLayer From,
    VideoLayer To)
{
    /// <summary>Gets a parameter value, or <paramref name="fallback"/> if it is not set.</summary>
    public double Get(string name, double fallback = 0) =>
        Parameters.TryGetValue(name, out double value) ? value : fallback;
}

/// <summary>
/// A pure description of how to render one composited frame at a given time: the target size and the
/// ordered layers (bottom→top, disabled tracks already removed). This is the output of the render
/// graph's <em>resolution</em> step and the input to its execution; it is fully serializable and
/// trivially unit-testable headlessly (ARCHITECTURE.md §5).
/// </summary>
/// <param name="Resolution">Target canvas size.</param>
/// <param name="Time">The timeline time this plan was resolved for.</param>
/// <param name="Layers">Layers to composite, bottom→top.</param>
public sealed record VideoFramePlan(Resolution Resolution, Timecode Time, IReadOnlyList<VideoLayer> Layers);

/// <summary>
/// One resolved audio layer for a buffer. The gain is given at both ends of the buffer so the mixer
/// can apply a linear ramp across it (fades, ARCHITECTURE.md §6); for a constant gain the two values
/// are equal.
/// </summary>
/// <param name="MediaRefId">Source to pull PCM from.</param>
/// <param name="SourceStart">Time within the source corresponding to the start of the buffer.</param>
/// <param name="GainStartLinear">Linear gain at the start of the buffer.</param>
/// <param name="GainEndLinear">Linear gain at the end of the buffer.</param>
/// <param name="SpeedRatio">Playback speed (source time per timeline time, PLAN.md step 21). 1/1 = normal; the
/// mixer resamples the source PCM by this factor. Defaults to 1/1 so non-retimed callers are unaffected.</param>
/// <param name="NestedPlan">The child sequence's resolved audio plan (a nested-sequence layer, PLAN.md step 23):
/// the mixer mixes it recursively, then applies this layer's gain envelope. <see langword="null"/> for an
/// ordinary media layer.</param>
/// <param name="PanLeft">Left-channel pan/balance gain in [0, 1] (PLAN.md step 30). 1.0 (the default) is the
/// centred / mono case — applied by the mixer on top of the gain ramp for a stereo output.</param>
/// <param name="PanRight">Right-channel pan/balance gain in [0, 1] (PLAN.md step 30). 1.0 = centred.</param>
public sealed record AudioLayer(
    MediaRefId MediaRefId,
    Timecode SourceStart,
    double GainStartLinear,
    double GainEndLinear,
    Rational SpeedRatio,
    AudioBufferPlan? NestedPlan = null,
    double PanLeft = 1.0,
    double PanRight = 1.0);

/// <summary>
/// Restricts an audio buffer plan to a measurement scope (PLAN.md step 30 loudness normalization). With the
/// defaults the plan is the normal full mix; a non-default scope isolates one track and/or forces unity gain at
/// a level so a scope's <em>raw</em> loudness can be measured (then normalized by setting that level's gain).
/// Applies only at the top level of the measured sequence — nested sub-mixes always use their full gains.
/// </summary>
/// <param name="OnlyTrack">If set, only this audio track contributes (its own mute/solo/enabled are ignored so
/// the track's content can be measured regardless of the current mix state).</param>
/// <param name="UnityTrackGain">If true, track gain is forced to 0 dB (measure a track's content before its gain).</param>
/// <param name="UnityMasterGain">If true, the project master gain is forced to 0 dB (measure before the master).</param>
public sealed record AudioPlanScope(
    AudioTrack? OnlyTrack = null,
    bool UnityTrackGain = false,
    bool UnityMasterGain = false);

/// <summary>
/// A pure description of how to fill one audio output buffer: which source spans to sum and at what
/// gain. The mixer (Sprocket.Audio) executes it; Core only resolves it.
/// </summary>
/// <param name="BufferStart">Timeline time at the start of the buffer.</param>
/// <param name="BufferDuration">Length of the buffer.</param>
/// <param name="Layers">Audio layers to sum.</param>
/// <param name="MasterGainLinear">Master output gain (linear) to apply after summing.</param>
public sealed record AudioBufferPlan(
    Timecode BufferStart,
    Timecode BufferDuration,
    IReadOnlyList<AudioLayer> Layers,
    double MasterGainLinear);
