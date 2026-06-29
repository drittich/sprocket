using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Covers the generator / adjustment-layer model added in PLAN.md step 19: the synthetic clip kinds, the
/// generator spec, the catalog, and that a blade split preserves a synthetic clip's nature.
/// </summary>
public class GeneratorTests
{
    [Fact]
    public void CreateGenerator_Is_A_Generator_Clip_With_The_Spec_And_Duration()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).SetString(GeneratorParamNames.Text, "Hi");
        Clip clip = Clip.CreateGenerator(spec, Timecode.FromSeconds(3), Timecode.FromSeconds(10));

        Assert.Equal(ClipKind.Generator, clip.Kind);
        Assert.Same(spec, clip.Generator);
        Assert.Equal(Timecode.Zero, clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(3), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(13), clip.TimelineEnd);
        Assert.Equal(default, clip.MediaRefId); // no source media
    }

    [Fact]
    public void CreateAdjustment_Is_An_Adjustment_Clip_With_No_Generator()
    {
        Clip clip = Clip.CreateAdjustment(Timecode.FromSeconds(5), Timecode.Zero);
        Assert.Equal(ClipKind.Adjustment, clip.Kind);
        Assert.Null(clip.Generator);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);
    }

    [Fact]
    public void Media_Clip_Defaults_To_Media_Kind()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero);
        Assert.Equal(ClipKind.Media, clip.Kind);
        Assert.Null(clip.Generator);
    }

    [Fact]
    public void GeneratorSpec_Clone_Is_Independent_For_Strings_And_Params()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title)
            .SetString(GeneratorParamNames.Text, "A")
            .Set(GeneratorParamNames.FontSize, 0.1);
        GeneratorSpec copy = spec.Clone();
        copy.SetString(GeneratorParamNames.Text, "B");
        copy.Set(GeneratorParamNames.FontSize, 0.2);

        Assert.Equal("A", spec.GetString(GeneratorParamNames.Text));
        Assert.Equal(0.1, spec.Parameters[GeneratorParamNames.FontSize].Evaluate(Timecode.Zero), 6);
        Assert.Equal("B", copy.GetString(GeneratorParamNames.Text));
    }

    [Fact]
    public void Catalog_Builds_Default_Generator_Clips()
    {
        GeneratorDescriptor title = Assert.Single(GeneratorCatalog.BuiltIns, d => d.Id == GeneratorTypeIds.Title);
        Clip clip = title.CreateClip(GeneratorCatalog.DefaultDuration, Timecode.Zero);

        Assert.Equal(ClipKind.Generator, clip.Kind);
        Assert.Equal(GeneratorTypeIds.Title, clip.Generator!.GeneratorTypeId);
        Assert.Equal("Title", clip.Generator.GetString(GeneratorParamNames.Text)); // default text
        Assert.Equal("Title", GeneratorCatalog.DisplayName(GeneratorTypeIds.Title));
    }

    [Fact]
    public void Split_Preserves_Generator_Kind_And_Copies_The_Spec()
    {
        var track = new VideoTrack();
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF112233");
        Clip clip = Clip.CreateGenerator(spec, Timecode.FromSeconds(4), Timecode.Zero);
        track.Clips.Add(clip);

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(1));
        split.Apply();

        Assert.Equal(2, track.Clips.Count);
        Clip right = split.RightClip;
        Assert.Equal(ClipKind.Generator, right.Kind);
        Assert.NotSame(spec, right.Generator); // a copy, not shared
        Assert.Equal("#FF112233", right.Generator!.GetString(GeneratorParamNames.Color));
        Assert.Equal(Timecode.FromSeconds(1), right.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(3), right.Duration);

        split.Revert();
        Assert.Single(track.Clips);
        Assert.Equal(Timecode.FromSeconds(4), clip.Duration);
    }
}
