namespace Sprocket.Core.Model;

/// <summary>
/// Well-known built-in generator type ids (PLAN.md step 19). A generator produces a video frame procedurally
/// instead of decoding source media — the render graph drives it through the generator seam exactly as it drives
/// a decoded layer (ARCHITECTURE.md §5). Plugins would use their own namespaced ids, like effects (§13).
/// </summary>
public static class GeneratorTypeIds
{
    /// <summary>A solid colour matte filling the frame. Parameter: <see cref="GeneratorParamNames.Color"/>.</summary>
    public const string SolidColor = "builtin.gen.solidcolor";

    /// <summary>
    /// A centred text/title over a (optionally transparent) background. Parameters:
    /// <see cref="GeneratorParamNames.Text"/>, <see cref="GeneratorParamNames.Color"/>,
    /// <see cref="GeneratorParamNames.BackgroundColor"/>, and <see cref="GeneratorParamNames.FontSize"/>.
    /// </summary>
    public const string Title = "builtin.gen.title";
}

/// <summary>Well-known parameter names used by the built-in generators. Colours are <c>#AARRGGBB</c> hex strings.</summary>
public static class GeneratorParamNames
{
    /// <summary>Foreground colour as <c>#AARRGGBB</c> hex (matte fill / text colour). A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string Color = "color";

    /// <summary>The title text. A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string Text = "text";

    /// <summary>Background colour behind the title as <c>#AARRGGBB</c> hex (use alpha 0 for transparent). A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string BackgroundColor = "backgroundColor";

    /// <summary>Title font size as a fraction of the frame height (e.g. 0.12). A numeric <see cref="GeneratorSpec.Parameters"/> entry.</summary>
    public const string FontSize = "fontSize";
}

/// <summary>
/// The procedural source of a <see cref="ClipKind.Generator"/> clip (PLAN.md step 19): a generator type id plus
/// its parameters. String parameters (text, colours) live in <see cref="Strings"/>; numeric, animatable ones
/// (e.g. font size) live in <see cref="Parameters"/> as <see cref="AnimatableValue"/>s — the same mechanism
/// effects use, so a generator parameter can keyframe. Plain data with no native handles, so it serializes with
/// the project and is evaluated by the render graph at the frame's time; the Render layer owns the actual drawing.
/// </summary>
public sealed class GeneratorSpec
{
    /// <summary>Creates a generator spec of the given type.</summary>
    public GeneratorSpec(string generatorTypeId)
    {
        if (string.IsNullOrWhiteSpace(generatorTypeId))
            throw new ArgumentException("Generator type id is required.", nameof(generatorTypeId));
        GeneratorTypeId = generatorTypeId;
    }

    /// <summary>The generator type id, e.g. <see cref="GeneratorTypeIds.Title"/>.</summary>
    public string GeneratorTypeId { get; }

    /// <summary>String parameters by name (text, colour hex). See <see cref="GeneratorParamNames"/>.</summary>
    public Dictionary<string, string> Strings { get; } = new();

    /// <summary>Numeric, animatable parameters by name, each an <see cref="AnimatableValue"/>.</summary>
    public Dictionary<string, AnimatableValue> Parameters { get; } = new();

    /// <summary>Sets a string parameter (fluent).</summary>
    public GeneratorSpec SetString(string name, string value)
    {
        Strings[name] = value;
        return this;
    }

    /// <summary>Sets a numeric parameter to a constant (fluent).</summary>
    public GeneratorSpec Set(string name, double value)
    {
        Parameters[name] = AnimatableValue.Constant(value);
        return this;
    }

    /// <summary>Sets a numeric parameter to an animatable value (fluent).</summary>
    public GeneratorSpec Set(string name, AnimatableValue value)
    {
        Parameters[name] = value;
        return this;
    }

    /// <summary>Gets a string parameter, or <paramref name="fallback"/> if it is not set.</summary>
    public string GetString(string name, string fallback = "") =>
        Strings.TryGetValue(name, out string? value) ? value : fallback;

    /// <summary>
    /// A copy with the same type and parameters. <see cref="AnimatableValue"/> is immutable so numeric entries are
    /// shared by reference; the maps are fresh. Used when a blade split copies a generator clip (PLAN.md step 13).
    /// </summary>
    public GeneratorSpec Clone()
    {
        var copy = new GeneratorSpec(GeneratorTypeId);
        foreach ((string name, string value) in Strings)
            copy.Strings[name] = value;
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value;
        return copy;
    }
}
