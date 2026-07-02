using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// HSL qualifier / secondary correction (PLAN.md step 34): keys a hue / saturation / luma range —
/// each bound softened smoothstep-style, the standard qualifier shape (Resolve's qualifier, Lumetri's
/// HSL Secondary) — and applies the grade (hue shift, saturation, exposure) only where the key holds,
/// blending by the mask so soft edges grade partially. <c>Show Mask</c> previews the key as greyscale
/// (white = fully keyed), the universal matte-preview convention. Display-referred, premultiplied-safe,
/// registry path (PLAN.md step 33).
/// </summary>
public sealed class HslQualifierEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float hueCenter;   // degrees [0, 360)
uniform float hueWidth;    // degrees half-width
uniform float hueSoft;     // degrees falloff beyond the width
uniform float satLow;
uniform float satHigh;
uniform float lumaLow;
uniform float lumaHigh;
uniform float rangeSoft;   // sat/luma falloff outside the range
uniform float hueShift;    // degrees
uniform float satGain;     // 1 = unchanged
uniform float exposure;    // stops
uniform float showMask;    // >= 0.5 previews the key

float3 rgb2hsv(float3 c) {
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = mix(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = mix(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-7;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 hsv2rgb(float3 c) {
    float3 p = abs(fract(c.xxx + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(float3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

// 1 inside [lo, hi], falling to 0 over `soft` outside either bound.
float rangeMask(float v, float lo, float hi, float soft) {
    float s = max(soft, 1.0e-4);
    return clamp((v - (lo - s)) / s, 0.0, 1.0) * clamp(((hi + s) - v) / s, 0.0, 1.0);
}

half4 main(float2 coord) {
    half4 p4 = src.eval(coord);
    float a = float(p4.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 c = clamp(float3(p4.rgb) / a, 0.0, 1.0);
    float3 hsv = rgb2hsv(c);
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));

    // Hue distance with wraparound; a width of 180° keys every hue.
    float dh = abs(hsv.x * 360.0 - hueCenter);
    dh = min(dh, 360.0 - dh);
    float soft = max(hueSoft, 1.0e-3);
    float hueMask = hueWidth >= 180.0 ? 1.0 : clamp((hueWidth + soft - dh) / soft, 0.0, 1.0);

    float mask = hueMask
               * rangeMask(hsv.y, satLow, satHigh, rangeSoft)
               * rangeMask(luma, lumaLow, lumaHigh, rangeSoft);

    if (showMask >= 0.5) {
        return half4(half3(mask * a), p4.a);
    }

    float3 graded = hsv;
    graded.x = fract(graded.x + hueShift / 360.0 + 1.0);
    graded.y = clamp(graded.y * satGain, 0.0, 1.0);
    float3 outRgb = clamp(hsv2rgb(graded) * exp2(exposure), 0.0, 1.0);
    return half4(half3(mix(c, outRgb, mask) * a), p4.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.HslQualifier)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.HslQualifier}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("hueCenter", (float)effect.Get(EffectParamNames.HueCenter, 0.0));
        uniforms.Set("hueWidth", (float)Math.Clamp(effect.Get(EffectParamNames.HueWidth, 60.0), 0.0, 180.0));
        uniforms.Set("hueSoft", (float)Math.Clamp(effect.Get(EffectParamNames.HueSoftness, 20.0), 0.0, 90.0));
        uniforms.Set("satLow", (float)Math.Clamp(effect.Get(EffectParamNames.SatLow, 0.0), 0.0, 1.0));
        uniforms.Set("satHigh", (float)Math.Clamp(effect.Get(EffectParamNames.SatHigh, 1.0), 0.0, 1.0));
        uniforms.Set("lumaLow", (float)Math.Clamp(effect.Get(EffectParamNames.LumaLow, 0.0), 0.0, 1.0));
        uniforms.Set("lumaHigh", (float)Math.Clamp(effect.Get(EffectParamNames.LumaHigh, 1.0), 0.0, 1.0));
        uniforms.Set("rangeSoft", (float)Math.Clamp(effect.Get(EffectParamNames.RangeSoftness, 0.1), 0.0, 0.5));
        uniforms.Set("hueShift", (float)effect.Get(EffectParamNames.HueShift, 0.0));
        uniforms.Set("satGain", (float)Math.Max(0.0, effect.Get(EffectParamNames.Saturation, 1.0)));
        uniforms.Set("exposure", (float)effect.Get(EffectParamNames.Exposure, 0.0));
        uniforms.Set("showMask", (float)effect.Get(EffectParamNames.ShowMask, 0.0));
    }
}
