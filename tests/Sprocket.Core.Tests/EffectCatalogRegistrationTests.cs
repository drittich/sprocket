using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The plugin-registration surface added to <see cref="EffectCatalog"/> by PLAN.md step 33: plugin
/// descriptors join <see cref="EffectCatalog.All"/> (browsers/Inspector), resolve via
/// <see cref="EffectCatalog.Find"/>, and — for audio-category descriptors — route through
/// <see cref="EffectTypeIds.IsAudio"/> to the mixer chain. The catalog is process-global static state, so
/// every test unregisters what it registered (try/finally) and uses ids unique to this file.
/// </summary>
public sealed class EffectCatalogRegistrationTests
{
    private static EffectDescriptor Video(string id) => new(
        id, "Test Video", EffectCategory.Color, "test", [new EffectParameterDescriptor("amount", "Amount", 1.0, 0.0, 2.0)]);

    private static EffectDescriptor Audio(string id) => new(
        id, "Test Audio", EffectCategory.Audio, "test", []);

    [Fact]
    public void Register_AddsToAllAndFind_Unregister_Removes()
    {
        const string id = "plugin.cat.video";
        Assert.True(EffectCatalog.Register(Video(id)));
        try
        {
            Assert.Contains(EffectCatalog.All, d => d.Id == id);
            Assert.NotNull(EffectCatalog.Find(id));
            Assert.Equal("Test Video", EffectCatalog.DisplayName(id));
            Assert.Contains(EffectCatalog.InCategory(EffectCategory.Color), d => d.Id == id);
        }
        finally
        {
            Assert.True(EffectCatalog.Unregister(id));
        }
        Assert.Null(EffectCatalog.Find(id));
        Assert.DoesNotContain(EffectCatalog.All, d => d.Id == id);
    }

    [Fact]
    public void Register_DuplicateId_IsRefused()
    {
        const string id = "plugin.cat.dup";
        Assert.True(EffectCatalog.Register(Video(id)));
        try
        {
            Assert.False(EffectCatalog.Register(Audio(id)));
            Assert.Equal(EffectCategory.Color, EffectCatalog.Find(id)!.Category); // the first registration stands
        }
        finally
        {
            EffectCatalog.Unregister(id);
        }
    }

    [Fact]
    public void Register_ReservedBuiltinPrefix_IsRefused()
    {
        Assert.False(EffectCatalog.Register(Video("builtin.cat.hijack")));
        Assert.Null(EffectCatalog.Find("builtin.cat.hijack"));
    }

    [Fact]
    public void Unregister_BuiltinOrUnknown_ReturnsFalse()
    {
        Assert.False(EffectCatalog.Unregister(EffectTypeIds.Brightness)); // built-ins are not in the plugin list
        Assert.NotNull(EffectCatalog.Find(EffectTypeIds.Brightness));     // …and stay present
        Assert.False(EffectCatalog.Unregister("plugin.cat.never.registered"));
    }

    [Fact]
    public void IsAudio_RegisteredAudioPlugin_RoutesToMixer()
    {
        const string id = "plugin.cat.audiofx";
        Assert.False(EffectTypeIds.IsAudio(id)); // unregistered → video chain (pass-through no-op)
        Assert.True(EffectCatalog.Register(Audio(id)));
        try
        {
            Assert.True(EffectTypeIds.IsAudio(id));
        }
        finally
        {
            EffectCatalog.Unregister(id);
        }
        Assert.False(EffectTypeIds.IsAudio(id));
    }

    [Fact]
    public void IsAudio_RegisteredVideoPlugin_StaysOnVideoChain()
    {
        const string id = "plugin.cat.videofx";
        Assert.True(EffectCatalog.Register(Video(id)));
        try
        {
            Assert.False(EffectTypeIds.IsAudio(id));
        }
        finally
        {
            EffectCatalog.Unregister(id);
        }
    }

    [Fact]
    public void AcesFilmic_IsARegisteredBuiltin_WithExposureParameter()
    {
        EffectDescriptor? aces = EffectCatalog.Find(EffectTypeIds.AcesFilmic);
        Assert.NotNull(aces);
        Assert.Equal(EffectCategory.Color, aces!.Category);
        EffectParameterDescriptor p = Assert.Single(aces.Parameters);
        Assert.Equal(EffectParamNames.Exposure, p.Name);
        Assert.Equal(0.0, aces.CreateInstance().Parameters[EffectParamNames.Exposure].Evaluate(Timing.Timecode.Zero));
    }

    // ── The colour grading toolset (PLAN.md step 34) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(EffectTypeIds.WhiteBalance, 2)]
    [InlineData(EffectTypeIds.ColorWheels, 12)]
    [InlineData(EffectTypeIds.Curves, 20)]
    [InlineData(EffectTypeIds.HslQualifier, 12)]
    public void GradingEffects_AreColorBuiltins_OnTheVideoChain(string id, int parameterCount)
    {
        EffectDescriptor? d = EffectCatalog.Find(id);
        Assert.NotNull(d);
        Assert.Equal(EffectCategory.Color, d!.Category);
        Assert.Equal(parameterCount, d.Parameters.Count);
        Assert.False(EffectTypeIds.IsAudio(id)); // grading routes to the shader chain
    }

    [Theory]
    [InlineData(EffectTypeIds.WhiteBalance)]
    [InlineData(EffectTypeIds.ColorWheels)]
    [InlineData(EffectTypeIds.Curves)]
    public void GradingEffects_DefaultInstancesAreNeutral(string id)
    {
        // A freshly added grading effect must be a visual no-op: every default is its neutral value (0,
        // except the qualifier's range bounds/gains, covered by the descriptor test above).
        EffectDescriptor d = EffectCatalog.Find(id)!;
        foreach (EffectParameterDescriptor p in d.Parameters)
            Assert.Equal(0.0, p.Default);
    }

    [Fact]
    public void Color_GainsVibrance_DefaultNeutral()
    {
        EffectDescriptor color = EffectCatalog.Find(EffectTypeIds.Color)!;
        EffectParameterDescriptor vibrance = Assert.Single(color.Parameters, p => p.Name == EffectParamNames.Vibrance);
        Assert.Equal(0.0, vibrance.Default);
    }
}
