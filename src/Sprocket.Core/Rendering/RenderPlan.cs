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

/// <summary>What produces a <see cref="VideoLayer"/>'s pixels (PLAN.md step 19).</summary>
public enum LayerKind
{
    /// <summary>Decoded source media fetched via <see cref="IFrameSource{TImage}"/>.</summary>
    Media,

    /// <summary>Drawn procedurally from a <see cref="VideoLayer.Generator"/>.</summary>
    Generator,

    /// <summary>An adjustment layer: its effects apply to the composite of the layers already drawn beneath it.</summary>
    Adjustment,
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
public sealed record VideoLayer(
    MediaRefId MediaRefId,
    Timecode SourceTime,
    IReadOnlyList<ResolvedEffect> Effects,
    double Opacity,
    BlendMode BlendMode,
    LayerKind Kind = LayerKind.Media,
    ResolvedGenerator? Generator = null);

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
public sealed record AudioLayer(
    MediaRefId MediaRefId,
    Timecode SourceStart,
    double GainStartLinear,
    double GainEndLinear,
    Rational SpeedRatio);

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
