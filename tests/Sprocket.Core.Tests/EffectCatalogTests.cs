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
}
