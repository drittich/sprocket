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
/// The registry of effect descriptors (ARCHITECTURE.md §4/§7/§13). <see cref="BuiltIns"/> holds the built-in
/// effects; plugin-contributed effects (PLAN.md step 33) are added at load time via <see cref="Register"/> and
/// removed on unload via <see cref="Unregister"/>, so every browser and the Inspector draw from one combined
/// list (<see cref="All"/>) rather than hard-coding the built-ins.
/// </summary>
public static class EffectCatalog
{
    /// <summary>All built-in effect descriptors, in display order.</summary>
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
            "Exposure, contrast, saturation, and vibrance adjustment.",
            [
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -3.0, 3.0, 0.1, "EV"),
                new EffectParameterDescriptor(EffectParamNames.Contrast, "Contrast", 1.0, 0.0, 2.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 1.0, 0.0, 2.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Vibrance, "Vibrance", 0.0, -1.0, 1.0, 0.05),
            ]),

        // ── Colour grading toolset (PLAN.md step 34) — SkSL registry effects, like ACES Filmic. ──
        new EffectDescriptor(
            EffectTypeIds.WhiteBalance,
            "White Balance",
            EffectCategory.Color,
            "Temperature and tint correction, applied in linear light.",
            [
                new EffectParameterDescriptor(EffectParamNames.Temperature, "Temperature", 0.0, -100.0, 100.0, 1.0),
                new EffectParameterDescriptor(EffectParamNames.Tint, "Tint", 0.0, -100.0, 100.0, 1.0),
            ]),

        new EffectDescriptor(
            EffectTypeIds.ColorWheels,
            "Color Wheels",
            EffectCategory.Color,
            "Three-way grade: lift (shadows), gamma (mids), gain (highlights), master + RGB.",
            [
                new EffectParameterDescriptor(EffectParamNames.LiftMaster, "Lift", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.LiftR, "Lift R", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.LiftG, "Lift G", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.LiftB, "Lift B", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GammaMaster, "Gamma", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GammaR, "Gamma R", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GammaG, "Gamma G", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GammaB, "Gamma B", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GainMaster, "Gain", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GainR, "Gain R", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GainG, "Gain G", 0.0, -1.0, 1.0, 0.005),
                new EffectParameterDescriptor(EffectParamNames.GainB, "Gain B", 0.0, -1.0, 1.0, 0.005),
            ]),

        new EffectDescriptor(
            EffectTypeIds.Curves,
            "Curves",
            EffectCategory.Color,
            "Parametric RGB + per-channel curves: five points offset the identity per channel.",
            [
                new EffectParameterDescriptor(EffectParamNames.CurveMasterBlacks, "RGB Blacks", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterShadows, "RGB Shadows", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterMids, "RGB Mids", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterHighlights, "RGB Highlights", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterWhites, "RGB Whites", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveRedBlacks, "Red Blacks", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveRedShadows, "Red Shadows", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveRedMids, "Red Mids", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveRedHighlights, "Red Highlights", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveRedWhites, "Red Whites", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenBlacks, "Green Blacks", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenShadows, "Green Shadows", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenMids, "Green Mids", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenHighlights, "Green Highlights", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenWhites, "Green Whites", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueBlacks, "Blue Blacks", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueShadows, "Blue Shadows", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueMids, "Blue Mids", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueHighlights, "Blue Highlights", 0.0, -1.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueWhites, "Blue Whites", 0.0, -1.0, 1.0, 0.01),
            ]),

        new EffectDescriptor(
            EffectTypeIds.HslQualifier,
            "HSL Qualifier",
            EffectCategory.Color,
            "Keys a hue/saturation/luma range and grades only the keyed pixels.",
            [
                new EffectParameterDescriptor(EffectParamNames.HueCenter, "Hue Center", 0.0, 0.0, 360.0, 1.0, "°"),
                new EffectParameterDescriptor(EffectParamNames.HueWidth, "Hue Width", 60.0, 0.0, 180.0, 1.0, "°"),
                new EffectParameterDescriptor(EffectParamNames.HueSoftness, "Hue Softness", 20.0, 0.0, 90.0, 1.0, "°"),
                new EffectParameterDescriptor(EffectParamNames.SatLow, "Sat Low", 0.0, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.SatHigh, "Sat High", 1.0, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.LumaLow, "Luma Low", 0.0, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.LumaHigh, "Luma High", 1.0, 0.0, 1.0, 0.01),
                new EffectParameterDescriptor(EffectParamNames.RangeSoftness, "Softness", 0.1, 0.0, 0.5, 0.01),
                new EffectParameterDescriptor(EffectParamNames.HueShift, "Hue Shift", 0.0, -180.0, 180.0, 1.0, "°"),
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 1.0, 0.0, 2.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -3.0, 3.0, 0.1, "EV"),
                new EffectParameterDescriptor(EffectParamNames.ShowMask, "Show Mask", 0.0, 0.0, 1.0, 1.0),
            ]),

        new EffectDescriptor(
            EffectTypeIds.AcesFilmic,
            "ACES Filmic",
            EffectCategory.Color,
            "Scene-linear ACES filmic tone mapping (RRT + ODT fit) with exposure.",
            [
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -8.0, 8.0, 0.1, "EV"),
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

        // ── Audio chain stages (PLAN.md step 31) — executed by the mixer, not the shader pipeline. ──
        new EffectDescriptor(
            EffectTypeIds.AudioGain,
            "Gain / Pan",
            EffectCategory.Audio,
            "Adjusts level and stereo balance.",
            [
                new EffectParameterDescriptor(EffectParamNames.GainDb, "Gain", 0.0, -24.0, 24.0, 0.5, "dB"),
                new EffectParameterDescriptor(EffectParamNames.Pan, "Pan", 0.0, -1.0, 1.0, 0.05),
            ]),

        new EffectDescriptor(
            EffectTypeIds.AudioEq,
            "Parametric EQ",
            EffectCategory.Audio,
            "Three-band EQ: low shelf, mid peak, high shelf.",
            [
                new EffectParameterDescriptor(EffectParamNames.LowGainDb, "Low Gain", 0.0, -15.0, 15.0, 0.5, "dB"),
                new EffectParameterDescriptor(EffectParamNames.LowFreq, "Low Freq", 100.0, 20.0, 500.0, 5.0, "Hz"),
                new EffectParameterDescriptor(EffectParamNames.MidGainDb, "Mid Gain", 0.0, -15.0, 15.0, 0.5, "dB"),
                new EffectParameterDescriptor(EffectParamNames.MidFreq, "Mid Freq", 1000.0, 200.0, 8000.0, 50.0, "Hz"),
                new EffectParameterDescriptor(EffectParamNames.MidQ, "Mid Q", 1.0, 0.3, 8.0, 0.1),
                new EffectParameterDescriptor(EffectParamNames.HighGainDb, "High Gain", 0.0, -15.0, 15.0, 0.5, "dB"),
                new EffectParameterDescriptor(EffectParamNames.HighFreq, "High Freq", 8000.0, 2000.0, 16000.0, 100.0, "Hz"),
            ]),

        new EffectDescriptor(
            EffectTypeIds.AudioCompressor,
            "Compressor",
            EffectCategory.Audio,
            "Evens out dynamics: attenuates peaks above the threshold.",
            [
                new EffectParameterDescriptor(EffectParamNames.ThresholdDb, "Threshold", -18.0, -60.0, 0.0, 0.5, "dB"),
                new EffectParameterDescriptor(EffectParamNames.Ratio, "Ratio", 4.0, 1.0, 20.0, 0.5),
                new EffectParameterDescriptor(EffectParamNames.AttackMs, "Attack", 10.0, 0.1, 200.0, 1.0, "ms"),
                new EffectParameterDescriptor(EffectParamNames.ReleaseMs, "Release", 100.0, 10.0, 1000.0, 10.0, "ms"),
                new EffectParameterDescriptor(EffectParamNames.MakeupDb, "Make-up", 0.0, 0.0, 24.0, 0.5, "dB"),
            ]),

        new EffectDescriptor(
            EffectTypeIds.AudioReverb,
            "Reverb",
            EffectCategory.Audio,
            "Adds room ambience (Freeverb-style).",
            [
                new EffectParameterDescriptor(EffectParamNames.RoomSize, "Room Size", 0.5, 0.0, 1.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Damping, "Damping", 0.5, 0.0, 1.0, 0.05),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05),
            ]),
    ];

    // Plugin-registered descriptors (PLAN.md step 33). Swapped atomically as a whole array so readers
    // (including the render graph's per-frame IsAudio routing) never see a partially-mutated list.
    private static readonly object RegistrationGate = new();
    private static volatile EffectDescriptor[] _registered = [];

    /// <summary>Built-in plus plugin-registered descriptors, built-ins first, in registration order.</summary>
    public static IReadOnlyList<EffectDescriptor> All
    {
        get
        {
            EffectDescriptor[] registered = _registered;
            return registered.Length == 0 ? BuiltIns : [.. BuiltIns, .. registered];
        }
    }

    /// <summary>
    /// Registers a plugin effect descriptor (PLAN.md step 33). Returns <see langword="false"/> — leaving the
    /// catalog unchanged — if the id is already taken or uses the reserved <c>builtin.</c> prefix.
    /// </summary>
    public static bool Register(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (RegistrationGate)
        {
            if (descriptor.Id.StartsWith("builtin.", StringComparison.Ordinal) || Find(descriptor.Id) is not null)
                return false;
            _registered = [.. _registered, descriptor];
            return true;
        }
    }

    /// <summary>Removes a plugin-registered descriptor (built-ins cannot be removed). Returns whether it was present.</summary>
    public static bool Unregister(string effectTypeId)
    {
        lock (RegistrationGate)
        {
            EffectDescriptor[] next = _registered.Where(d => d.Id != effectTypeId).ToArray();
            if (next.Length == _registered.Length)
                return false;
            _registered = next;
            return true;
        }
    }

    /// <summary>The descriptors in a given category, in display order.</summary>
    public static IEnumerable<EffectDescriptor> InCategory(EffectCategory category) =>
        All.Where(d => d.Category == category);

    /// <summary>Looks up a descriptor by effect type id, or returns <see langword="null"/> if it is not registered.</summary>
    public static EffectDescriptor? Find(string effectTypeId)
    {
        foreach (EffectDescriptor d in BuiltIns)
            if (d.Id == effectTypeId)
                return d;
        foreach (EffectDescriptor d in _registered)
            if (d.Id == effectTypeId)
                return d;
        return null;
    }

    /// <summary>A friendly display name for an effect type id, falling back to the id itself for unknown (plugin) ids.</summary>
    public static string DisplayName(string effectTypeId) => Find(effectTypeId)?.DisplayName ?? effectTypeId;
}
