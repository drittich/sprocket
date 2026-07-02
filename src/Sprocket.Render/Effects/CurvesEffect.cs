using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Parametric curves (PLAN.md step 34): an RGB (master) curve plus per-channel red/green/blue curves.
/// Each curve is five points at fixed inputs 0 / ¼ / ½ / ¾ / 1 (blacks / shadows / mids / highlights /
/// whites) whose values <em>offset the identity</em> (all-zero = pass-through) — the Lightroom-style
/// parametric form, chosen because it keeps every point an animatable scalar the type-driven Inspector
/// and keyframe model already handle (a freeform point-list curve editor is a UI follow-up, not a new
/// render seam). Offsets are interpolated with a Catmull-Rom spline, so a single moved point bends the
/// curve smoothly. Master applies first, then the channel curves — matching how RGB + channel curves
/// stack in Premiere / Resolve. Display-referred, premultiplied-safe, registry path (PLAN.md step 33).
/// </summary>
public sealed class CurvesEffect : IVideoEffect
{
    // Each channel's five offsets arrive as a float4 (points 0–3) + float (point 4): SkSL vectors cannot
    // be indexed dynamically, so the spline picks its segment by branching instead of an array lookup.
    private const string Sksl = @"
uniform shader src;
uniform float4 mP; uniform float mQ;   // master (RGB) curve offsets
uniform float4 rP; uniform float rQ;   // red
uniform float4 gP; uniform float gQ;   // green
uniform float4 bP; uniform float bQ;   // blue

// Cubic Hermite over one segment b→c with Catmull-Rom tangents from the neighbours a/d.
float seg(float f, float a, float b, float c, float d) {
    float m1 = (c - a) * 0.5;
    float m2 = (d - b) * 0.5;
    float f2 = f * f;
    float f3 = f2 * f;
    return (2.0 * f3 - 3.0 * f2 + 1.0) * b + (f3 - 2.0 * f2 + f) * m1
         + (-2.0 * f3 + 3.0 * f2) * c + (f3 - f2) * m2;
}

// The interpolated offset at x for points (0, p.x) (0.25, p.y) (0.5, p.z) (0.75, p.w) (1, q); the ends
// use a mirrored virtual neighbour, giving the one-sided tangent.
float curveOffset(float x, float4 p, float q) {
    float t = clamp(x, 0.0, 1.0) * 4.0;
    if (t < 1.0) { return seg(t,       2.0 * p.x - p.y, p.x, p.y, p.z); }
    if (t < 2.0) { return seg(t - 1.0, p.x, p.y, p.z, p.w); }
    if (t < 3.0) { return seg(t - 2.0, p.y, p.z, p.w, q); }
    return seg(min(t - 3.0, 1.0), p.z, p.w, q, 2.0 * q - p.w);
}

float applyCurve(float x, float4 p, float q) {
    return clamp(x + curveOffset(x, p, q), 0.0, 1.0);
}

half4 main(float2 coord) {
    half4 c4 = src.eval(coord);
    float a = float(c4.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 c = clamp(float3(c4.rgb) / a, 0.0, 1.0);
    c = float3(applyCurve(c.r, mP, mQ), applyCurve(c.g, mP, mQ), applyCurve(c.b, mP, mQ));
    c = float3(applyCurve(c.r, rP, rQ), applyCurve(c.g, gP, gQ), applyCurve(c.b, bP, bQ));
    return half4(half3(c * a), c4.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Curves)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Curves}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        Bind(effect, uniforms, "mP", "mQ",
            EffectParamNames.CurveMasterBlacks, EffectParamNames.CurveMasterShadows, EffectParamNames.CurveMasterMids,
            EffectParamNames.CurveMasterHighlights, EffectParamNames.CurveMasterWhites);
        Bind(effect, uniforms, "rP", "rQ",
            EffectParamNames.CurveRedBlacks, EffectParamNames.CurveRedShadows, EffectParamNames.CurveRedMids,
            EffectParamNames.CurveRedHighlights, EffectParamNames.CurveRedWhites);
        Bind(effect, uniforms, "gP", "gQ",
            EffectParamNames.CurveGreenBlacks, EffectParamNames.CurveGreenShadows, EffectParamNames.CurveGreenMids,
            EffectParamNames.CurveGreenHighlights, EffectParamNames.CurveGreenWhites);
        Bind(effect, uniforms, "bP", "bQ",
            EffectParamNames.CurveBlueBlacks, EffectParamNames.CurveBlueShadows, EffectParamNames.CurveBlueMids,
            EffectParamNames.CurveBlueHighlights, EffectParamNames.CurveBlueWhites);
    }

    private static void Bind(ResolvedEffect effect, IUniformWriter uniforms, string vec, string last,
        string p0, string p1, string p2, string p3, string p4)
    {
        uniforms.Set(vec,
        [
            (float)Math.Clamp(effect.Get(p0, 0.0), -1.0, 1.0),
            (float)Math.Clamp(effect.Get(p1, 0.0), -1.0, 1.0),
            (float)Math.Clamp(effect.Get(p2, 0.0), -1.0, 1.0),
            (float)Math.Clamp(effect.Get(p3, 0.0), -1.0, 1.0),
        ]);
        uniforms.Set(last, (float)Math.Clamp(effect.Get(p4, 0.0), -1.0, 1.0));
    }
}
