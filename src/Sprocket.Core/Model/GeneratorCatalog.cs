using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// A browsable description of one generator type (PLAN.md step 19): its stable id (<see cref="GeneratorTypeIds"/>),
/// a display name, a one-line description, and a factory for a default <see cref="GeneratorSpec"/>. The Project
/// panel lists these as synthetic bin items the user can drop on the timeline, mirroring <see cref="EffectCatalog"/>
/// for effects; a plugin generator (later) would register here too.
/// </summary>
/// <param name="Id">The generator type id (matches <see cref="GeneratorSpec.GeneratorTypeId"/>).</param>
/// <param name="DisplayName">Human-readable name for the browser.</param>
/// <param name="Description">A one-line summary shown under the name.</param>
public sealed record GeneratorDescriptor(string Id, string DisplayName, string Description)
{
    private readonly Func<GeneratorSpec>? _factory;

    /// <summary>Internal ctor capturing the default-spec factory.</summary>
    internal GeneratorDescriptor(string id, string displayName, string description, Func<GeneratorSpec> factory)
        : this(id, displayName, description) => _factory = factory;

    /// <summary>Builds a fresh <see cref="GeneratorSpec"/> with this type's default parameters.</summary>
    public GeneratorSpec CreateSpec() => _factory?.Invoke() ?? new GeneratorSpec(Id);

    /// <summary>
    /// Builds a fresh generator <see cref="Clip"/> of this type, <paramref name="duration"/> long, placed at
    /// <paramref name="timelineStart"/> — what dropping the bin item on a track produces.
    /// </summary>
    public Clip CreateClip(Timecode duration, Timecode timelineStart) =>
        Clip.CreateGenerator(CreateSpec(), duration, timelineStart);
}

/// <summary>
/// The registry of built-in generators (PLAN.md step 19). The Project panel and "insert generator" actions list
/// over this, so a new generator's bin entry falls out of registering it here rather than hard-coding the UI.
/// </summary>
public static class GeneratorCatalog
{
    /// <summary>The default length a freshly inserted generator/adjustment clip spans (NLE convention ~5 s).</summary>
    public static Timecode DefaultDuration { get; } = Timecode.FromSeconds(5);

    /// <summary>All registered generator descriptors, in display order.</summary>
    public static IReadOnlyList<GeneratorDescriptor> BuiltIns { get; } =
    [
        new GeneratorDescriptor(
            GeneratorTypeIds.Title, "Title", "Centred text over a transparent background.",
            () => new GeneratorSpec(GeneratorTypeIds.Title)
                .SetString(GeneratorParamNames.Text, "Title")
                .SetString(GeneratorParamNames.Color, "#FFFFFFFF")
                .SetString(GeneratorParamNames.BackgroundColor, "#00000000")
                .Set(GeneratorParamNames.FontSize, 0.12)),

        new GeneratorDescriptor(
            GeneratorTypeIds.SolidColor, "Color Matte", "A solid colour fill.",
            () => new GeneratorSpec(GeneratorTypeIds.SolidColor)
                .SetString(GeneratorParamNames.Color, "#FF1E6FFF")),
    ];

    /// <summary>Looks up a descriptor by generator type id, or <see langword="null"/> if it is not registered.</summary>
    public static GeneratorDescriptor? Find(string generatorTypeId) =>
        BuiltIns.FirstOrDefault(d => d.Id == generatorTypeId);

    /// <summary>A friendly display name for a generator type id, falling back to the id for unknown ids.</summary>
    public static string DisplayName(string generatorTypeId) => Find(generatorTypeId)?.DisplayName ?? generatorTypeId;
}
