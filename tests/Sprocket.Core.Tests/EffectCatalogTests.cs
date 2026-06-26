using System.Linq;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests the built-in effect registry (PLAN.md step 15): the Effects browser and (later) the Inspector and
/// plugin host enumerate effects through <see cref="EffectCatalog"/>, so its descriptors and factories must be
/// correct.
/// </summary>
public class EffectCatalogTests
{
    [Fact]
    public void BuiltIns_Contains_The_Slice_Effects()
    {
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Brightness);
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Fade);
    }

    [Fact]
    public void Find_Returns_Descriptor_By_Id()
    {
        EffectDescriptor? brightness = EffectCatalog.Find(EffectTypeIds.Brightness);
        Assert.NotNull(brightness);
        Assert.Equal("Brightness", brightness!.DisplayName);
        Assert.Equal(EffectCategory.Color, brightness.Category);
    }

    [Fact]
    public void Find_Returns_Null_For_Unknown_Id()
    {
        Assert.Null(EffectCatalog.Find("plugin.unknown.effect"));
        // DisplayName falls back to the id itself so an unregistered (plugin) effect still labels in the UI.
        Assert.Equal("plugin.unknown.effect", EffectCatalog.DisplayName("plugin.unknown.effect"));
    }

    [Fact]
    public void CreateInstance_Builds_An_Instance_With_Default_Params()
    {
        EffectInstance brightness = EffectCatalog.Find(EffectTypeIds.Brightness)!.CreateInstance();
        Assert.Equal(EffectTypeIds.Brightness, brightness.EffectTypeId);
        Assert.True(brightness.Parameters.ContainsKey(EffectParamNames.Amount));

        EffectInstance fade = EffectCatalog.Find(EffectTypeIds.Fade)!.CreateInstance();
        Assert.Equal(EffectTypeIds.Fade, fade.EffectTypeId);
        Assert.True(fade.Parameters.ContainsKey(EffectParamNames.Opacity));

        // Each call yields a fresh instance (adding to a clip's stack must not share state).
        Assert.NotSame(brightness, EffectCatalog.Find(EffectTypeIds.Brightness)!.CreateInstance());
    }

    [Fact]
    public void InCategory_Filters_By_Category()
    {
        Assert.All(EffectCatalog.InCategory(EffectCategory.Color), d => Assert.Equal(EffectCategory.Color, d.Category));
        Assert.Contains(EffectCatalog.InCategory(EffectCategory.Color), d => d.Id == EffectTypeIds.Brightness);
    }

    // ── Step 16: Transform & Color effects, type-driven parameter descriptors ──────────────────────────

    [Fact]
    public void BuiltIns_Contains_The_Step16_Effects()
    {
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Transform);
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Color);

        Assert.Equal(EffectCategory.Video, EffectCatalog.Find(EffectTypeIds.Transform)!.Category);
        Assert.Equal(EffectCategory.Color, EffectCatalog.Find(EffectTypeIds.Color)!.Category);
    }

    [Fact]
    public void Transform_Exposes_Its_Geometric_Parameters()
    {
        EffectDescriptor transform = EffectCatalog.Find(EffectTypeIds.Transform)!;
        string[] names = transform.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.Scale, EffectParamNames.PositionX, EffectParamNames.PositionY,
                EffectParamNames.Rotation, EffectParamNames.AnchorX, EffectParamNames.AnchorY,
                EffectParamNames.Opacity,
            },
            names);
    }

    [Fact]
    public void Color_Exposes_Exposure_Contrast_Saturation()
    {
        EffectDescriptor color = EffectCatalog.Find(EffectTypeIds.Color)!;
        string[] names = color.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { EffectParamNames.Exposure, EffectParamNames.Contrast, EffectParamNames.Saturation },
            names);
    }

    [Fact]
    public void CreateInstance_Sets_Every_Descriptor_Parameter_To_Its_Default()
    {
        EffectDescriptor transform = EffectCatalog.Find(EffectTypeIds.Transform)!;
        EffectInstance instance = transform.CreateInstance();

        // Every declared parameter is present, and at its declared default value.
        foreach (EffectParameterDescriptor p in transform.Parameters)
        {
            Assert.True(instance.Parameters.ContainsKey(p.Name));
            Assert.Equal(p.Default, instance.Parameters[p.Name].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
        }

        // Scale/opacity default to identity; anchor to centre.
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.Scale].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
        Assert.Equal(0.5, instance.Parameters[EffectParamNames.AnchorX].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
    }

    [Fact]
    public void Parameter_Defaults_Are_Within_Their_Declared_Range()
    {
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectParameterDescriptor p in d.Parameters)
            {
                Assert.True(p.Min <= p.Max, $"{d.Id}.{p.Name} has Min > Max");
                Assert.InRange(p.Default, p.Min, p.Max);
            }
    }
}
