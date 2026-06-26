namespace Sprocket.Core.Model;

/// <summary>The broad grouping an effect falls under, used to organise the Effects/Audio browsers (PLAN.md step 15).</summary>
public enum EffectCategory
{
    /// <summary>Geometry / compositing on the video frame (alpha, transform).</summary>
    Video,

    /// <summary>Colour / tone adjustments on the video frame (brightness, exposure, contrast).</summary>
    Color,

    /// <summary>Effects that act on the audio signal (gain, fade gain).</summary>
    Audio,
}

/// <summary>
/// A type-driven description of one editable effect parameter (PLAN.md step 16): its stable name (matches
/// the key in <see cref="EffectInstance.Parameters"/>), a display label, the default value a fresh instance
/// gets, the slider range, an editing step for numeric nudge, and an optional unit suffix. The Inspector
/// builds a slider + numeric editor per descriptor, so a new effect's UI falls out of its registration with
/// no bespoke control code (and a plugin gets the same treatment, ARCHITECTURE.md §4).
/// </summary>
/// <param name="Name">The parameter key (matches <see cref="EffectParamNames"/>).</param>
/// <param name="DisplayName">Human-readable label shown in the Inspector.</param>
/// <param name="Default">The value a freshly created instance is given.</param>
/// <param name="Min">Minimum of the slider range.</param>
/// <param name="Max">Maximum of the slider range.</param>
/// <param name="Step">Suggested increment for numeric nudge / arrow keys.</param>
/// <param name="Unit">Optional unit suffix for display (e.g. <c>"°"</c>, <c>"EV"</c>).</param>
public sealed record EffectParameterDescriptor(
    string Name,
    string DisplayName,
    double Default,
    double Min,
    double Max,
    double Step = 0.01,
    string? Unit = null);

/// <summary>
/// A browsable description of one effect type: its stable id (<see cref="EffectTypeIds"/>), a display name,
/// a category, a one-line description, and the ordered list of its editable <see cref="EffectParameterDescriptor"/>s.
/// This is the "effect registry" the Effects browser lists over (PLAN.md step 15); the Inspector (step 16)
/// builds its per-effect controls from <see cref="Parameters"/>, and a future plugin host (step 23) registers
/// here too, so every browser and the Inspector draw from one list rather than hard-coding the built-ins.
/// </summary>
/// <param name="Id">The effect type id (matches <see cref="EffectInstance.EffectTypeId"/>).</param>
/// <param name="DisplayName">Human-readable name for the browser.</param>
/// <param name="Category">Which browser section this effect belongs to.</param>
/// <param name="Description">A one-line summary shown under the name.</param>
/// <param name="Parameters">The effect's editable parameters, in display order.</param>
public sealed record EffectDescriptor(
    string Id,
    string DisplayName,
    EffectCategory Category,
    string Description,
    IReadOnlyList<EffectParameterDescriptor> Parameters)
{
    /// <summary>
    /// Builds a fresh <see cref="EffectInstance"/> of this type with every parameter set to its
    /// <see cref="EffectParameterDescriptor.Default"/>. Each call yields an independent instance.
    /// </summary>
    public EffectInstance CreateInstance()
    {
        var instance = new EffectInstance(Id);
        foreach (EffectParameterDescriptor p in Parameters)
            instance.Set(p.Name, p.Default);
        return instance;
    }
}

/// <summary>
/// The registry of built-in effects (ARCHITECTURE.md §4/§7). Holds the slice effects (brightness, fade) plus
/// the step-16 Transform and Color effects; plugin-contributed effects (step 23) register here as they land,
/// so every browser and the Inspector draw from one list rather than hard-coding the built-ins.
/// </summary>
public static class EffectCatalog
{
    /// <summary>All registered effect descriptors, in display order.</summary>
    public static IReadOnlyList<EffectDescriptor> BuiltIns { get; } =
    [
        new EffectDescriptor(
            EffectTypeIds.Transform,
            "Transform",
            EffectCategory.Video,
            "Scale, position, and rotate the layer around an anchor, with layer opacity.",
            [
                new EffectParameterDescriptor(EffectParamNames.Scale, "Scale", 1.0, 0.0, 4.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.PositionX, "Position X", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.PositionY, "Position Y", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.Rotation, "Rotation", 0.0, -180.0, 180.0, 1.0, "°"),
                new EffectParameterDescriptor(EffectParamNames.AnchorX, "Anchor X", 0.5, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.AnchorY, "Anchor Y", 0.5, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05),
            ]),

        new EffectDescriptor(
            EffectTypeIds.Color,
            "Color",
            EffectCategory.Color,
            "Exposure, contrast, and saturation adjustment.",
            [
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -3.0, 3.0, 0.1, "EV"),
                new EffectParameterDescriptor(EffectParamNames.Contrast, "Contrast", 1.0, 0.0, 2.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 1.0, 0.0, 2.0, 0.05),
            ]),

        new EffectDescriptor(
            EffectTypeIds.Brightness,
            "Brightness",
            EffectCategory.Color,
            "Multiplies the image brightness (1.0 = unchanged).",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 1.0, 0.0, 4.0, 0.05),
            ]),

        new EffectDescriptor(
            EffectTypeIds.Fade,
            "Fade",
            EffectCategory.Video,
            "Ramps opacity — drives video alpha and audio gain together.",
            [
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05),
            ]),
    ];

    /// <summary>The descriptors in a given category, in display order.</summary>
    public static IEnumerable<EffectDescriptor> InCategory(EffectCategory category) =>
        BuiltIns.Where(d => d.Category == category);

    /// <summary>Looks up a descriptor by effect type id, or returns <see langword="null"/> if it is not registered.</summary>
    public static EffectDescriptor? Find(string effectTypeId) =>
        BuiltIns.FirstOrDefault(d => d.Id == effectTypeId);

    /// <summary>A friendly display name for an effect type id, falling back to the id itself for unknown (plugin) ids.</summary>
    public static string DisplayName(string effectTypeId) => Find(effectTypeId)?.DisplayName ?? effectTypeId;
}
