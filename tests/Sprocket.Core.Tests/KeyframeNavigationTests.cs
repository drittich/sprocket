using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class KeyframeNavigationTests
{
    private static Clip ClipWithKeyframes()
    {
        // A clip on the timeline; keyframes are absolute timeline times. Two animated parameters across two
        // effects, so navigation must gather keyframes from the whole stack: opacity @1s,3s and exposure @2s.
        var clip = new Clip(new MediaRefId(Guid.NewGuid()), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);

        var fade = new EffectInstance(EffectTypeIds.Fade);
        fade.Set(EffectParamNames.Opacity, AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(1), 0.0),
            new Keyframe(Timecode.FromSeconds(3), 1.0),
        }));
        clip.Effects.Add(fade);

        var color = new EffectInstance(EffectTypeIds.Color);
        color.Set(EffectParamNames.Exposure, AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(2), 0.5),
        }));
        color.Set(EffectParamNames.Contrast, 1.0); // a constant parameter contributes no keyframes
        clip.Effects.Add(color);

        return clip;
    }

    [Fact]
    public void PreviousKeyframe_Finds_Latest_Before_Time_Across_All_Params()
    {
        Clip clip = ClipWithKeyframes();
        Assert.Equal(Timecode.FromSeconds(2), KeyframeNavigation.PreviousKeyframe(clip, Timecode.FromSeconds(2.5)));
        Assert.Equal(Timecode.FromSeconds(1), KeyframeNavigation.PreviousKeyframe(clip, Timecode.FromSeconds(2)));
    }

    [Fact]
    public void NextKeyframe_Finds_Earliest_After_Time_Across_All_Params()
    {
        Clip clip = ClipWithKeyframes();
        Assert.Equal(Timecode.FromSeconds(2), KeyframeNavigation.NextKeyframe(clip, Timecode.FromSeconds(1)));
        Assert.Equal(Timecode.FromSeconds(3), KeyframeNavigation.NextKeyframe(clip, Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Navigation_Is_Strict_So_It_Does_Not_Stick_On_The_Current_Keyframe()
    {
        Clip clip = ClipWithKeyframes();
        // Sitting exactly on the 2s keyframe, prev/next must move off it.
        Assert.Equal(Timecode.FromSeconds(1), KeyframeNavigation.PreviousKeyframe(clip, Timecode.FromSeconds(2)));
        Assert.Equal(Timecode.FromSeconds(3), KeyframeNavigation.NextKeyframe(clip, Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Returns_Null_When_There_Is_Nothing_In_That_Direction()
    {
        Clip clip = ClipWithKeyframes();
        Assert.Null(KeyframeNavigation.PreviousKeyframe(clip, Timecode.FromSeconds(1)));   // 1s is the first
        Assert.Null(KeyframeNavigation.NextKeyframe(clip, Timecode.FromSeconds(3)));       // 3s is the last
    }

    [Fact]
    public void HasKeyframes_Reflects_Whether_Any_Parameter_Is_Animated()
    {
        Assert.True(KeyframeNavigation.HasKeyframes(ClipWithKeyframes()));

        var plain = new Clip(new MediaRefId(Guid.NewGuid()), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);
        var brightness = new EffectInstance(EffectTypeIds.Brightness);
        brightness.Set(EffectParamNames.Amount, 1.2); // constant only
        plain.Effects.Add(brightness);
        Assert.False(KeyframeNavigation.HasKeyframes(plain));
    }
}
