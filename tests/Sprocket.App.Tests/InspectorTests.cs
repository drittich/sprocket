using System.Linq;
using Sprocket.App.Inspector;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the Inspector's pure helpers (PLAN.md step 16): value formatting and the
/// <see cref="AnimatableValue"/> editing/keyframe transforms. The control's slider/numeric binding rests on
/// these plus manual verification (the App is a UI-bound WinExe), mirroring the step-12 TimelineMath split.
/// </summary>
public class InspectorTests
{
    // ── InspectorFormat ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, null, "1")]
    [InlineData(1.15, null, "1.15")]
    [InlineData(0.3333, null, "0.333")]
    [InlineData(-2.5, null, "-2.5")]
    public void Format_Trims_To_Three_Decimals(double value, string? unit, string expected) =>
        Assert.Equal(expected, InspectorFormat.Value(value, unit));

    [Fact]
    public void Format_Appends_Units()
    {
        Assert.Equal("45°", InspectorFormat.Value(45, "°"));   // degrees abut the number
        Assert.Equal("1.5 EV", InspectorFormat.Value(1.5, "EV")); // other units are spaced
    }

    // ── AnimatableEditing: scalar set ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SetValueAt_Constant_Replaces_The_Constant()
    {
        AnimatableValue result = AnimatableEditing.SetValueAt(AnimatableValue.Constant(1.0), Timecode.FromSeconds(2), 0.4);
        Assert.False(result.IsAnimated);
        Assert.Equal(0.4, result.Evaluate(Timecode.Zero), 5);
    }

    [Fact]
    public void SetValueAt_Animated_Upserts_A_Keyframe_At_The_Playhead()
    {
        AnimatableValue animated = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 0.0),
            new Keyframe(Timecode.FromSeconds(4), 1.0),
        ]);

        AnimatableValue result = AnimatableEditing.SetValueAt(animated, Timecode.FromSeconds(2), 0.25);
        Assert.True(result.IsAnimated);
        Assert.Equal(3, result.Keyframes.Count);
        Assert.Equal(0.25, result.Evaluate(Timecode.FromSeconds(2)), 5);
    }

    // ── AnimatableEditing: keyframe toggle ──────────────────────────────────────────────────────────────

    [Fact]
    public void EnableKeyframing_Turns_A_Constant_Into_A_Single_Keyframe_At_The_Playhead()
    {
        Timecode t = Timecode.FromSeconds(3);
        AnimatableValue result = AnimatableEditing.EnableKeyframing(AnimatableValue.Constant(0.7), t);

        Assert.True(result.IsAnimated);
        Assert.Single(result.Keyframes);
        Assert.Equal(t.Ticks, result.Keyframes[0].Time.Ticks);
        Assert.Equal(0.7, result.Keyframes[0].Value, 5);
    }

    [Fact]
    public void EnableKeyframing_Leaves_An_Already_Animated_Value_Unchanged()
    {
        AnimatableValue animated = AnimatableValue.Animated([new Keyframe(Timecode.Zero, 0.2)]);
        Assert.Same(animated, AnimatableEditing.EnableKeyframing(animated, Timecode.FromSeconds(1)));
    }

    [Fact]
    public void DisableKeyframing_Collapses_To_A_Constant_At_The_Playhead()
    {
        AnimatableValue animated = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 0.0),
            new Keyframe(Timecode.FromSeconds(4), 1.0),
        ]);

        AnimatableValue result = AnimatableEditing.DisableKeyframing(animated, Timecode.FromSeconds(2));
        Assert.False(result.IsAnimated);
        Assert.Equal(0.5, result.Evaluate(Timecode.Zero), 5); // value at t=2 is the 0→1 midpoint
    }

    // ── AnimatableEditing: upsert ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpsertKeyframe_Replaces_The_Keyframe_At_The_Same_Time_Preserving_Others()
    {
        Timecode mid = Timecode.FromSeconds(2);
        AnimatableValue animated = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 0.0),
            new Keyframe(mid, 0.5),
            new Keyframe(Timecode.FromSeconds(4), 1.0),
        ]);

        AnimatableValue result = AnimatableEditing.UpsertKeyframe(animated, mid, 0.9);
        Assert.Equal(3, result.Keyframes.Count); // replaced, not added
        Assert.Equal(0.9, result.Keyframes.Single(k => k.Time.Ticks == mid.Ticks).Value, 5);
    }

    [Fact]
    public void UpsertKeyframe_Adds_A_New_Keyframe_When_None_Exists_At_The_Time()
    {
        AnimatableValue animated = AnimatableValue.Animated([new Keyframe(Timecode.Zero, 0.0)]);
        AnimatableValue result = AnimatableEditing.UpsertKeyframe(animated, Timecode.FromSeconds(5), 1.0);
        Assert.Equal(2, result.Keyframes.Count);
        Assert.Equal(1.0, result.Evaluate(Timecode.FromSeconds(5)), 5);
    }
}
