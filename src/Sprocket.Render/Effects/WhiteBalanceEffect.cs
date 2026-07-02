using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// White balance (PLAN.md step 34): temperature / tint correction as per-channel gains applied in
/// linear light — the Lumetri / Resolve convention (positive temperature warms: red up, blue down;
/// positive tint pushes magenta: green down). The gain vector is normalised to unit Rec.709 luma so
/// correcting the balance never shifts overall exposure. Runs on the registry path like ACES
/// (PLAN.md step 33): unpremultiply → linear → gains → sRGB encode → repremultiply, all in the
/// shader so preview and export are identical (§5) and no pixels cross to managed code (§1).
/// </summary>
public sealed class WhiteBalanceEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float temperature; // [-1, 1], positive = warmer
uniform float tint;        // [-1, 1], positive = magenta

float3 srgbToLinear(float3 c) {
    float3 lo = c / 12.92;
    float3 hi = pow((c + 0.055) / 1.055, float3(2.4));
    return mix(lo, hi, step(0.04045, c));
}

float3 linearToSrgb(float3 c) {
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, float3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(0.0031308, c));
}

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 gains = max(float3(1.0 + 0.30 * temperature + 0.15 * tint,
                              1.0 - 0.30 * tint,
                              1.0 - 0.30 * temperature + 0.15 * tint), 0.0);
    // Normalise to unit luma so the correction is chromatic only (a grey card keeps its brightness).
    gains /= max(dot(gains, float3(0.2126, 0.7152, 0.0722)), 1e-4);

    float3 c = clamp(float3(p.rgb) / a, 0.0, 1.0);
    float3 lin = clamp(srgbToLinear(c) * gains, 0.0, 1.0);
    return half4(half3(linearToSrgb(lin) * a), p.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.WhiteBalance)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.WhiteBalance}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        // The descriptor's [-100, 100] slider scale (the Lumetri convention) normalises to [-1, 1] here.
        uniforms.Set("temperature", (float)Math.Clamp(effect.Get(EffectParamNames.Temperature, 0.0) / 100.0, -1.0, 1.0));
        uniforms.Set("tint", (float)Math.Clamp(effect.Get(EffectParamNames.Tint, 0.0) / 100.0, -1.0, 1.0));
    }
}
