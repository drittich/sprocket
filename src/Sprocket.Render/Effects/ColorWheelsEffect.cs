using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Lift / gamma / gain colour wheels (PLAN.md step 34): the three-way tonal grade of every professional
/// grading page, using the standard video LGG transfer — per channel,
/// <c>out = clamp(in·gain + lift·(1−in), 0, 1) ^ (1/gamma)</c> — so lift moves the shadows (pivoting on
/// white), gain the highlights (pivoting on black), and gamma the mids as a power curve. Each wheel has
/// a master plus R/G/B components that sum (master 0 / channel 0 = neutral), matching how Resolve's
/// wheel + ring pair resolves to per-channel values. Display-referred (operates on the sRGB-encoded
/// signal, like the wheels in Resolve's primaries), premultiplied-safe, registry path (PLAN.md step 33).
/// </summary>
public sealed class ColorWheelsEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float3 lift;   // per-channel lift, 0 = neutral
uniform float3 gain;   // per-channel gain offset, 0 = neutral (multiplier 1 + gain)
uniform float3 gamma;  // per-channel gamma offset, 0 = neutral (power 1 / (1 + gamma))

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 c = clamp(float3(p.rgb) / a, 0.0, 1.0);
    c = clamp(c * (float3(1.0) + gain) + lift * (float3(1.0) - c), 0.0, 1.0);
    c = pow(c, float3(1.0) / max(float3(1.0) + gamma, 0.05));
    return half4(half3(c * a), p.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.ColorWheels)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.ColorWheels}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("lift", Wheel(effect, EffectParamNames.LiftMaster, EffectParamNames.LiftR, EffectParamNames.LiftG, EffectParamNames.LiftB));
        uniforms.Set("gain", Wheel(effect, EffectParamNames.GainMaster, EffectParamNames.GainR, EffectParamNames.GainG, EffectParamNames.GainB));
        uniforms.Set("gamma", Wheel(effect, EffectParamNames.GammaMaster, EffectParamNames.GammaR, EffectParamNames.GammaG, EffectParamNames.GammaB));
    }

    /// <summary>One wheel's per-channel values: master + channel component each, clamped to the wheel's [-1, 1] throw.</summary>
    private static float[] Wheel(ResolvedEffect effect, string master, string r, string g, string b)
    {
        double m = effect.Get(master, 0.0);
        return
        [
            (float)Math.Clamp(m + effect.Get(r, 0.0), -1.0, 1.0),
            (float)Math.Clamp(m + effect.Get(g, 0.0), -1.0, 1.0),
            (float)Math.Clamp(m + effect.Get(b, 0.0), -1.0, 1.0),
        ];
    }
}
