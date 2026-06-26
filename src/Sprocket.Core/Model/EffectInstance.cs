namespace Sprocket.Core.Model;

/// <summary>
/// Well-known built-in effect type ids. Plugins use their own namespaced ids
/// (e.g. <c>"plugin.acme.glow"</c>) — see ARCHITECTURE.md §4, §13.
/// </summary>
public static class EffectTypeIds
{
    /// <summary>Brightness multiply. Parameter: <see cref="EffectParamNames.Amount"/>.</summary>
    public const string Brightness = "builtin.brightness";

    /// <summary>
    /// Fade. Drives video alpha (in the shader) and audio gain (in the mixer) from a single
    /// parameter, <see cref="EffectParamNames.Opacity"/>, animated 1→0 (or 0→1) over a range.
    /// </summary>
    public const string Fade = "builtin.fade";
}

/// <summary>Well-known parameter names used by the built-in effects.</summary>
public static class EffectParamNames
{
    /// <summary>Brightness multiplier (1.0 = unchanged).</summary>
    public const string Amount = "amount";

    /// <summary>Opacity / gain multiplier in [0, 1] — used by <see cref="EffectTypeIds.Fade"/>.</summary>
    public const string Opacity = "opacity";
}

/// <summary>
/// One effect in a clip's ordered effect stack (ARCHITECTURE.md §4). Holds the effect's type id and
/// its parameters as <see cref="AnimatableValue"/>s; the render graph evaluates the parameters at the
/// frame's time and hands the result to the Render layer, which owns the actual shader.
/// </summary>
public sealed class EffectInstance
{
    /// <summary>Creates an effect instance of the given type.</summary>
    public EffectInstance(string effectTypeId)
    {
        if (string.IsNullOrWhiteSpace(effectTypeId))
            throw new ArgumentException("Effect type id is required.", nameof(effectTypeId));
        EffectTypeId = effectTypeId;
    }

    /// <summary>The effect type id, e.g. <see cref="EffectTypeIds.Brightness"/>.</summary>
    public string EffectTypeId { get; }

    /// <summary>Parameters by name, each an <see cref="AnimatableValue"/>.</summary>
    public Dictionary<string, AnimatableValue> Parameters { get; } = new();

    /// <summary>Sets a parameter to a constant value (fluent).</summary>
    public EffectInstance Set(string name, double value)
    {
        Parameters[name] = AnimatableValue.Constant(value);
        return this;
    }

    /// <summary>Sets a parameter to an animatable value (fluent).</summary>
    public EffectInstance Set(string name, AnimatableValue value)
    {
        Parameters[name] = value;
        return this;
    }

    /// <summary>
    /// A copy with the same type and parameters. <see cref="AnimatableValue"/> is immutable so the entries
    /// are shared by reference; only the parameter map is fresh. Used when a blade split copies a clip's
    /// effect stack onto the new right-hand half (PLAN.md step 13).
    /// </summary>
    public EffectInstance Clone()
    {
        var copy = new EffectInstance(EffectTypeId);
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value;
        return copy;
    }
}
