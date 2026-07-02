using Sprocket.Core.Timing;

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

    /// <summary>
    /// Geometric transform: scale, position, rotation around an anchor, plus layer opacity (PLAN.md step 16).
    /// Parameters: <see cref="EffectParamNames.Scale"/>, <see cref="EffectParamNames.PositionX"/>/
    /// <see cref="EffectParamNames.PositionY"/>, <see cref="EffectParamNames.Rotation"/>,
    /// <see cref="EffectParamNames.AnchorX"/>/<see cref="EffectParamNames.AnchorY"/>, and
    /// <see cref="EffectParamNames.Opacity"/>.
    /// </summary>
    public const string Transform = "builtin.transform";

    /// <summary>
    /// Colour / tone adjustment on the same per-pixel SkSL shape as brightness (PLAN.md step 16).
    /// Parameters: <see cref="EffectParamNames.Exposure"/> (stops), <see cref="EffectParamNames.Contrast"/>,
    /// and <see cref="EffectParamNames.Saturation"/>.
    /// </summary>
    public const string Color = "builtin.color";

    /// <summary>
    /// ACES filmic tone mapping (PLAN.md step 33): sRGB → scene-linear → exposure → the fitted ACES
    /// RRT + ODT curve → sRGB, all in the shader. Parameter: <see cref="EffectParamNames.Exposure"/> (stops).
    /// The first built-in realised through the <see cref="Sprocket.Core.Rendering.IVideoEffect"/> registry
    /// (the same path plugin effects use) rather than a hard-coded pipeline case.
    /// </summary>
    public const string AcesFilmic = "builtin.aces.filmic";

    /// <summary>
    /// White balance (PLAN.md step 34): temperature / tint gains applied in linear light, the standard
    /// first grading correction (Lumetri / Resolve convention: warm = positive temperature, magenta =
    /// positive tint). Parameters: <see cref="EffectParamNames.Temperature"/>, <see cref="EffectParamNames.Tint"/>.
    /// </summary>
    public const string WhiteBalance = "builtin.whitebalance";

    /// <summary>
    /// Lift / gamma / gain colour wheels (PLAN.md step 34): the three-way tonal grade every professional
    /// grading page centres on — lift moves shadows, gamma mids, gain highlights, each with a master and
    /// per-channel R/G/B component (<see cref="EffectParamNames.LiftMaster"/> … <see cref="EffectParamNames.GainB"/>).
    /// </summary>
    public const string ColorWheels = "builtin.colorwheels";

    /// <summary>
    /// Parametric curves (PLAN.md step 34): RGB (master) + per-channel red/green/blue curves, each a
    /// five-point parametric curve (blacks / shadows / mids / highlights / whites at fixed inputs
    /// 0 / ¼ / ½ / ¾ / 1) whose points offset the identity — the Lightroom-style parametric form, which
    /// keeps every point an animatable scalar (<see cref="EffectParamNames.CurveMasterBlacks"/> …).
    /// </summary>
    public const string Curves = "builtin.curves";

    /// <summary>
    /// HSL qualifier / secondary (PLAN.md step 34): keys a hue/saturation/luma range and grades only the
    /// keyed pixels (hue shift, saturation, exposure), with a mask preview — the standard secondary
    /// correction. Parameters: <see cref="EffectParamNames.HueCenter"/>, <see cref="EffectParamNames.HueWidth"/>,
    /// <see cref="EffectParamNames.HueSoftness"/>, <see cref="EffectParamNames.SatLow"/>/<see cref="EffectParamNames.SatHigh"/>,
    /// <see cref="EffectParamNames.LumaLow"/>/<see cref="EffectParamNames.LumaHigh"/>,
    /// <see cref="EffectParamNames.RangeSoftness"/>, <see cref="EffectParamNames.HueShift"/>,
    /// <see cref="EffectParamNames.Saturation"/>, <see cref="EffectParamNames.Exposure"/>,
    /// <see cref="EffectParamNames.ShowMask"/>.
    /// </summary>
    public const string HslQualifier = "builtin.hsl.qualify";

    /// <summary>
    /// Audio gain/pan (PLAN.md step 31): a static per-chain-stage gain (<see cref="EffectParamNames.GainDb"/>)
    /// and stereo balance (<see cref="EffectParamNames.Pan"/>), the simplest audio DSP stage.
    /// </summary>
    public const string AudioGain = "builtin.audio.gain";

    /// <summary>
    /// Three-band parametric EQ (PLAN.md step 31): low shelf, mid peak (with Q), high shelf — RBJ biquads.
    /// Parameters: <see cref="EffectParamNames.LowGainDb"/>/<see cref="EffectParamNames.LowFreq"/>,
    /// <see cref="EffectParamNames.MidGainDb"/>/<see cref="EffectParamNames.MidFreq"/>/<see cref="EffectParamNames.MidQ"/>,
    /// <see cref="EffectParamNames.HighGainDb"/>/<see cref="EffectParamNames.HighFreq"/>.
    /// </summary>
    public const string AudioEq = "builtin.audio.eq";

    /// <summary>
    /// Dynamic-range compressor (PLAN.md step 31): peak-envelope feed-forward design. Parameters:
    /// <see cref="EffectParamNames.ThresholdDb"/>, <see cref="EffectParamNames.Ratio"/>,
    /// <see cref="EffectParamNames.AttackMs"/>, <see cref="EffectParamNames.ReleaseMs"/>,
    /// <see cref="EffectParamNames.MakeupDb"/>.
    /// </summary>
    public const string AudioCompressor = "builtin.audio.compressor";

    /// <summary>
    /// Reverb (PLAN.md step 31): Freeverb-style comb/allpass network. Parameters:
    /// <see cref="EffectParamNames.RoomSize"/>, <see cref="EffectParamNames.Damping"/>,
    /// <see cref="EffectParamNames.Mix"/> (wet/dry).
    /// </summary>
    public const string AudioReverb = "builtin.audio.reverb";

    /// <summary>
    /// Whether an effect type id names an <b>audio</b> chain stage (PLAN.md step 31). The render graph uses
    /// this to split a clip's single effect stack: audio ids feed the mixer's DSP chain, everything else feeds
    /// the video shader chain (where unknown ids pass through). Built-in audio effects share the
    /// <c>builtin.audio.</c> prefix (the fast path); a plugin audio effect (PLAN.md step 33) is recognised by
    /// its registered <see cref="EffectCatalog"/> descriptor carrying <see cref="EffectCategory.Audio"/>.
    /// An unregistered (missing-plugin) audio id therefore routes to the video chain, where unknown ids
    /// pass through — a no-op either way.
    /// </summary>
    public static bool IsAudio(string effectTypeId) =>
        effectTypeId.StartsWith("builtin.audio.", StringComparison.Ordinal)
        || (!effectTypeId.StartsWith("builtin.", StringComparison.Ordinal)
            && EffectCatalog.Find(effectTypeId)?.Category == EffectCategory.Audio);
}

/// <summary>Well-known parameter names used by the built-in effects.</summary>
public static class EffectParamNames
{
    /// <summary>Brightness multiplier (1.0 = unchanged).</summary>
    public const string Amount = "amount";

    /// <summary>Opacity / gain multiplier in [0, 1] — used by <see cref="EffectTypeIds.Fade"/> and
    /// <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Opacity = "opacity";

    /// <summary>Uniform scale factor (1.0 = unchanged) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Scale = "scale";

    /// <summary>Horizontal position offset, as a fraction of the frame width — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string PositionX = "positionX";

    /// <summary>Vertical position offset, as a fraction of the frame height — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string PositionY = "positionY";

    /// <summary>Rotation in degrees, clockwise — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Rotation = "rotation";

    /// <summary>Anchor X in [0, 1] across the frame (0.5 = centre) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string AnchorX = "anchorX";

    /// <summary>Anchor Y in [0, 1] down the frame (0.5 = centre) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string AnchorY = "anchorY";

    /// <summary>Exposure in stops (0 = unchanged) — <see cref="EffectTypeIds.Color"/>.</summary>
    public const string Exposure = "exposure";

    /// <summary>Contrast around mid-grey (1.0 = unchanged) — <see cref="EffectTypeIds.Color"/>.</summary>
    public const string Contrast = "contrast";

    /// <summary>Saturation (1.0 = unchanged, 0 = greyscale) — <see cref="EffectTypeIds.Color"/> and the
    /// <see cref="EffectTypeIds.HslQualifier"/> correction.</summary>
    public const string Saturation = "saturation";

    /// <summary>Vibrance in [-1, 1] (0 = unchanged): saturation weighted toward already-muted colours —
    /// <see cref="EffectTypeIds.Color"/> (PLAN.md step 34).</summary>
    public const string Vibrance = "vibrance";

    /// <summary>Colour temperature in [-100, 100] (0 = neutral, positive = warmer) — <see cref="EffectTypeIds.WhiteBalance"/>.</summary>
    public const string Temperature = "temperature";

    /// <summary>Tint in [-100, 100] (0 = neutral, positive = magenta, negative = green) — <see cref="EffectTypeIds.WhiteBalance"/>.</summary>
    public const string Tint = "tint";

    // ── Lift / gamma / gain wheels (PLAN.md step 34) — each in [-1, 1], 0 = neutral. ──
    /// <summary>Lift (shadows) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftMaster = "liftMaster";
    /// <summary>Lift red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftR = "liftR";
    /// <summary>Lift green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftG = "liftG";
    /// <summary>Lift blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftB = "liftB";
    /// <summary>Gamma (mids) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaMaster = "gammaMaster";
    /// <summary>Gamma red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaR = "gammaR";
    /// <summary>Gamma green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaG = "gammaG";
    /// <summary>Gamma blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaB = "gammaB";
    /// <summary>Gain (highlights) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainMaster = "gainMaster";
    /// <summary>Gain red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainR = "gainR";
    /// <summary>Gain green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainG = "gainG";
    /// <summary>Gain blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainB = "gainB";

    // ── Parametric curves (PLAN.md step 34) — five points per channel at inputs 0/¼/½/¾/1, each an
    // output offset in [-1, 1] added to the identity (0 = unchanged). ──
    /// <summary>Master (RGB) curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterBlacks = "curveMasterBlacks";
    /// <summary>Master (RGB) curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterShadows = "curveMasterShadows";
    /// <summary>Master (RGB) curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterMids = "curveMasterMids";
    /// <summary>Master (RGB) curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterHighlights = "curveMasterHighlights";
    /// <summary>Master (RGB) curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterWhites = "curveMasterWhites";
    /// <summary>Red curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedBlacks = "curveRedBlacks";
    /// <summary>Red curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedShadows = "curveRedShadows";
    /// <summary>Red curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedMids = "curveRedMids";
    /// <summary>Red curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedHighlights = "curveRedHighlights";
    /// <summary>Red curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedWhites = "curveRedWhites";
    /// <summary>Green curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenBlacks = "curveGreenBlacks";
    /// <summary>Green curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenShadows = "curveGreenShadows";
    /// <summary>Green curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenMids = "curveGreenMids";
    /// <summary>Green curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenHighlights = "curveGreenHighlights";
    /// <summary>Green curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenWhites = "curveGreenWhites";
    /// <summary>Blue curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueBlacks = "curveBlueBlacks";
    /// <summary>Blue curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueShadows = "curveBlueShadows";
    /// <summary>Blue curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueMids = "curveBlueMids";
    /// <summary>Blue curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueHighlights = "curveBlueHighlights";
    /// <summary>Blue curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueWhites = "curveBlueWhites";

    // ── HSL qualifier (PLAN.md step 34). ──
    /// <summary>Keyed hue centre in degrees [0, 360) — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueCenter = "hueCenter";
    /// <summary>Keyed hue half-width in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueWidth = "hueWidth";
    /// <summary>Hue key edge softness in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueSoftness = "hueSoftness";
    /// <summary>Keyed saturation range lower bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string SatLow = "satLow";
    /// <summary>Keyed saturation range upper bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string SatHigh = "satHigh";
    /// <summary>Keyed luma range lower bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string LumaLow = "lumaLow";
    /// <summary>Keyed luma range upper bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string LumaHigh = "lumaHigh";
    /// <summary>Saturation/luma key edge softness in [0, 0.5] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string RangeSoftness = "rangeSoftness";
    /// <summary>Hue rotation applied to keyed pixels, in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueShift = "hueShift";
    /// <summary>Mask preview toggle (≥ 0.5 shows the key as greyscale) — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string ShowMask = "showMask";

    /// <summary>Gain in decibels (0 = unity) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string GainDb = "gainDb";

    /// <summary>Stereo balance in [-1, 1] (0 = centre) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string Pan = "pan";

    /// <summary>Low-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string LowGainDb = "lowGainDb";

    /// <summary>Low-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string LowFreq = "lowFreq";

    /// <summary>Mid-peak gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidGainDb = "midGainDb";

    /// <summary>Mid-peak centre frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidFreq = "midFreq";

    /// <summary>Mid-peak Q (bandwidth) — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidQ = "midQ";

    /// <summary>High-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string HighGainDb = "highGainDb";

    /// <summary>High-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string HighFreq = "highFreq";

    /// <summary>Compressor threshold in dBFS — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string ThresholdDb = "thresholdDb";

    /// <summary>Compression ratio (N:1) — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string Ratio = "ratio";

    /// <summary>Compressor attack time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string AttackMs = "attackMs";

    /// <summary>Compressor release time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string ReleaseMs = "releaseMs";

    /// <summary>Compressor make-up gain in dB — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string MakeupDb = "makeupDb";

    /// <summary>Reverb room size in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string RoomSize = "roomSize";

    /// <summary>Reverb high-frequency damping in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string Damping = "damping";

    /// <summary>Reverb wet/dry mix in [0, 1] (0 = dry only) — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string Mix = "mix";
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

    /// <summary>
    /// A clone whose animated parameters have every keyframe time shifted by <paramref name="delta"/> (see
    /// <see cref="AnimatableValue.Shifted"/>). Used when a clip is pasted/duplicated to a new timeline start so
    /// its keyframed effects (e.g. the default fade in/out) move with the clip instead of staying anchored to
    /// the original clip's timeline span. A zero delta is equivalent to <see cref="Clone"/>.
    /// </summary>
    public EffectInstance CloneShifted(Timecode delta)
    {
        var copy = new EffectInstance(EffectTypeId);
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value.Shifted(delta);
        return copy;
    }
}
