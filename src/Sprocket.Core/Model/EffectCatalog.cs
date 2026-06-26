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
/// A browsable description of one effect type: its stable id (<see cref="EffectTypeIds"/>), a display name,
/// a category, a one-line description, and a factory that builds a fresh <see cref="EffectInstance"/> with
/// sensible default parameters. This is the "effect registry" the Effects browser lists over (PLAN.md step 15);
/// it also gives the Inspector (step 16) and a future plugin host (step 23) a single place to enumerate effects.
/// </summary>
/// <param name="Id">The effect type id (matches <see cref="EffectInstance.EffectTypeId"/>).</param>
/// <param name="DisplayName">Human-readable name for the browser.</param>
/// <param name="Category">Which browser section this effect belongs to.</param>
/// <param name="Description">A one-line summary shown under the name.</param>
public sealed record EffectDescriptor(
    string Id,
    string DisplayName,
    EffectCategory Category,
    string Description,
    Func<EffectInstance> CreateInstance);

/// <summary>
/// The registry of built-in effects (ARCHITECTURE.md §4/§7). Today it holds the two slice effects — brightness
/// and fade; the Transform / Color effects (step 16) and plugin-contributed effects (step 23) register here as
/// they land, so every browser and inspector draws from one list rather than hard-coding the built-ins.
/// </summary>
public static class EffectCatalog
{
    /// <summary>All registered effect descriptors, in display order.</summary>
    public static IReadOnlyList<EffectDescriptor> BuiltIns { get; } =
    [
        new EffectDescriptor(
            EffectTypeIds.Brightness,
            "Brightness",
            EffectCategory.Color,
            "Multiplies the image brightness (1.0 = unchanged).",
            () => new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.0)),

        new EffectDescriptor(
            EffectTypeIds.Fade,
            "Fade",
            EffectCategory.Video,
            "Ramps opacity — drives video alpha and audio gain together.",
            () => new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, 1.0)),
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
