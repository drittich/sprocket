using System.Linq;
using Sprocket.App.Inspector;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the keyframe-lane editing helpers (PLAN.md step 16b): the new
/// <see cref="AnimatableEditing"/> transforms (move / remove / interpolation) and the pure
/// <see cref="KeyframeLaneMath"/> geometry. The lane control's drawing + pointer interaction rest on these +
/// manual verification (the App is a UI-bound WinExe).
/// </summary>
public class KeyframeEditingTests
{
    private static AnimatableValue ThreeKeys() => AnimatableValue.Animated(
    [
        new Keyframe(Timecode.FromSeconds(0), 0.0, Interpolation.Linear),
        new Keyframe(Timecode.FromSeconds(1), 1.0, Interpolation.Linear),
        new Keyframe(Timecode.FromSeconds(2), 0.0, Interpolation.Linear),
    ]);

    [Fact]
    public void MoveKeyframe_Reschedules_Preserving_Value_And_Order()
    {
        AnimatableValue moved = AnimatableEditing.MoveKeyframe(
            ThreeKeys(), Timecode.FromSeconds(1), Timecode.FromSeconds(1.5));

        Assert.Equal(3, moved.Keyframes.Count);
        // Still sorted; the moved keyframe kept its value (1.0) at the new time.
        Assert.Equal([0, 1.5, 2], moved.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
        Keyframe at = moved.Keyframes.Single(k => k.Time.Ticks == Timecode.FromSeconds(1.5).Ticks);
        Assert.Equal(1.0, at.Value);
    }

    [Fact]
    public void MoveKeyframe_Onto_Another_Overwrites_It()
    {
        AnimatableValue moved = AnimatableEditing.MoveKeyframe(
            ThreeKeys(), Timecode.FromSeconds(1), Timecode.FromSeconds(2));
        // The keyframe formerly at 2 (value 0) is replaced by the one moved there (value 1); 2 keyframes remain.
        Assert.Equal(2, moved.Keyframes.Count);
        Assert.Equal(1.0, moved.Evaluate(Timecode.FromSeconds(2))); // moved keyframe's value won
    }

    [Fact]
    public void MoveKeyframe_Is_A_NoOp_For_A_Missing_Source()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.MoveKeyframe(v, Timecode.FromSeconds(9), Timecode.FromSeconds(3)));
    }

    [Fact]
    public void RemoveKeyframe_Drops_One_And_Keeps_The_Rest()
    {
        AnimatableValue v = AnimatableEditing.RemoveKeyframe(ThreeKeys(), Timecode.FromSeconds(1));
        Assert.Equal([0, 2], v.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
    }

    [Fact]
    public void RemoveKeyframe_Of_The_Last_Collapses_To_Constant()
    {
        AnimatableValue one = AnimatableValue.Animated([new Keyframe(Timecode.FromSeconds(1), 0.7)]);
        AnimatableValue v = AnimatableEditing.RemoveKeyframe(one, Timecode.FromSeconds(1));
        Assert.False(v.IsAnimated);
        Assert.Equal(0.7, v.Evaluate(Timecode.Zero));
    }

    [Fact]
    public void SetInterpolation_Toggles_Hold_And_Linear()
    {
        AnimatableValue held = AnimatableEditing.SetInterpolation(
            ThreeKeys(), Timecode.FromSeconds(0), Interpolation.Hold);
        // With the first segment held, the value stays at 0 across [0,1) instead of ramping toward 1.
        Assert.Equal(0.0, held.Evaluate(Timecode.FromSeconds(0.5)));

        AnimatableValue back = AnimatableEditing.SetInterpolation(held, Timecode.FromSeconds(0), Interpolation.Linear);
        Assert.Equal(0.5, back.Evaluate(Timecode.FromSeconds(0.5)), 3); // linear again → halfway
    }

    // ── Premiere-parity keyframe ops (PLAN.md step 16d) ─────────────────────────────────────────────────

    [Fact]
    public void CycleInterpolation_Walks_Through_Every_Mode_And_Wraps()
    {
        AnimatableValue v = ThreeKeys(); // first keyframe is Linear
        Timecode t = Timecode.FromSeconds(0);
        Interpolation[] expected =
        [
            Interpolation.EaseIn, Interpolation.EaseOut, Interpolation.EaseInOut,
            Interpolation.Bezier, Interpolation.Hold, Interpolation.Linear, // wraps back around
        ];
        foreach (Interpolation mode in expected)
        {
            v = AnimatableEditing.CycleInterpolation(v, t);
            Assert.Equal(mode, v.Keyframes[0].Interpolation);
        }
    }

    [Fact]
    public void CycleInterpolation_Is_A_NoOp_When_No_Keyframe_Sits_There()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.CycleInterpolation(v, Timecode.FromSeconds(9)));
    }

    [Fact]
    public void NudgeKeyframes_Shifts_Only_The_Selected_Times_By_The_Delta()
    {
        long delta = Timecode.FromSeconds(0.5).Ticks;
        AnimatableValue v = AnimatableEditing.NudgeKeyframes(
            ThreeKeys(), new[] { Timecode.FromSeconds(1).Ticks }, delta);

        Assert.Equal([0, 1.5, 2], v.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
        // The nudged keyframe kept its value (1.0).
        Assert.Equal(1.0, v.Keyframes.Single(k => k.Time.Ticks == Timecode.FromSeconds(1.5).Ticks).Value);
    }

    [Fact]
    public void NudgeKeyframes_Moves_A_Whole_Multi_Selection_Together()
    {
        long delta = Timecode.FromSeconds(1).Ticks;
        AnimatableValue v = AnimatableEditing.NudgeKeyframes(
            ThreeKeys(), new[] { Timecode.FromSeconds(0).Ticks, Timecode.FromSeconds(1).Ticks }, delta);

        // 0→1 and 1→2; the original 2 is overwritten by the keyframe shifted onto it, so two remain.
        Assert.Equal([1, 2], v.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
        Assert.Equal(1.0, v.Keyframes.Single(k => k.Time.Ticks == Timecode.FromSeconds(2).Ticks).Value); // shifted won
    }

    [Fact]
    public void NudgeKeyframes_Is_A_NoOp_For_Zero_Delta_Or_No_Match()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.NudgeKeyframes(v, new[] { Timecode.FromSeconds(1).Ticks }, 0));
        Assert.Same(v, AnimatableEditing.NudgeKeyframes(v, new[] { Timecode.FromSeconds(9).Ticks }, 100));
    }

    [Fact]
    public void Copy_Then_Paste_Reproduces_Values_And_Interpolation_At_A_New_Origin()
    {
        AnimatableValue src = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.FromSeconds(1), 0.2, Interpolation.Hold),
            new Keyframe(Timecode.FromSeconds(2), 0.8, Interpolation.EaseInOut),
        ]);

        var copied = AnimatableEditing.CopyKeyframes(
            src, new[] { Timecode.FromSeconds(1).Ticks, Timecode.FromSeconds(2).Ticks });
        Assert.Equal(2, copied.Count);

        // Paste onto an empty (constant) parameter at t=5s: earliest lands at 5, the other keeps the +1s gap.
        AnimatableValue pasted = AnimatableEditing.PasteKeyframes(
            AnimatableValue.Constant(0), copied, Timecode.FromSeconds(5));

        Assert.True(pasted.IsAnimated);
        Assert.Equal([5, 6], pasted.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
        Assert.Equal(0.2, pasted.Keyframes[0].Value);
        Assert.Equal(Interpolation.Hold, pasted.Keyframes[0].Interpolation);
        Assert.Equal(0.8, pasted.Keyframes[1].Value);
        Assert.Equal(Interpolation.EaseInOut, pasted.Keyframes[1].Interpolation);
    }

    [Fact]
    public void PasteKeyframes_Is_A_NoOp_For_An_Empty_Clipboard()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.PasteKeyframes(v, [], Timecode.FromSeconds(5)));
    }

    [Fact]
    public void NextMode_Cycle_Covers_All_Modes_Exactly_Once_Before_Repeating()
    {
        var seen = new System.Collections.Generic.HashSet<Interpolation>();
        Interpolation m = Interpolation.Hold;
        for (int i = 0; i < 6; i++)
        {
            Assert.True(seen.Add(m), $"mode {m} repeated early");
            m = AnimatableEditing.NextMode(m);
        }
        Assert.Equal(6, seen.Count);            // Hold, Linear, EaseIn, EaseOut, EaseInOut, Bezier
        Assert.Equal(Interpolation.Hold, m);    // wrapped back to the start
    }

    // ── Custom Bezier velocity handles (PLAN.md step 16d, item 1) ───────────────────────────────────────

    [Fact]
    public void SetOutgoingHandle_Switches_The_Keyframe_To_Bezier_And_Stores_The_Handle()
    {
        var h = new BezierHandle(0.2, 0.0);
        AnimatableValue v = AnimatableEditing.SetOutgoingHandle(ThreeKeys(), Timecode.FromSeconds(0), h);
        Keyframe k = v.Keyframes[0];
        Assert.Equal(Interpolation.Bezier, k.Interpolation);
        Assert.Equal(h, k.EaseOut);
    }

    [Fact]
    public void SetIncomingHandle_Stores_The_Handle_Without_Changing_The_Mode()
    {
        var h = new BezierHandle(0.8, 1.0);
        AnimatableValue v = AnimatableEditing.SetIncomingHandle(ThreeKeys(), Timecode.FromSeconds(1), h);
        Keyframe k = v.Keyframes.Single(x => x.Time.Ticks == Timecode.FromSeconds(1).Ticks);
        Assert.Equal(Interpolation.Linear, k.Interpolation); // unchanged
        Assert.Equal(h, k.EaseIn);
    }

    [Fact]
    public void SetHandle_Is_A_NoOp_When_No_Keyframe_Sits_There()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.SetOutgoingHandle(v, Timecode.FromSeconds(9), new BezierHandle(0.5, 0.5)));
    }

    [Fact]
    public void Custom_Bezier_Handles_Shape_The_Evaluated_Curve()
    {
        // A near-flat-then-steep custom curve: control points pull the value low through most of the segment.
        var v = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.FromSeconds(0), 0.0, Interpolation.Bezier, EaseOut: new BezierHandle(0.9, 0.0)),
            new Keyframe(Timecode.FromSeconds(2), 1.0, EaseIn: new BezierHandle(1.0, 0.0)),
        ]);
        // Endpoints exact.
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(0)), 6);
        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(2)), 6);
        // With both control points held low/late, the midpoint sits well below the linear 0.5.
        Assert.True(v.Evaluate(Timecode.FromSeconds(1)) < 0.3,
            $"expected a slow-start curve, got {v.Evaluate(Timecode.FromSeconds(1))}");
    }

    // ── KeyframeGraphMath (value-graph geometry, item 1) ────────────────────────────────────────────────

    [Fact]
    public void YForValue_And_ValueForY_Round_Trip_With_Max_At_The_Top()
    {
        // max maps to y=0 (top), min to y=height (bottom).
        Assert.Equal(0, KeyframeGraphMath.YForValue(2.0, 0.0, 2.0, 100), 6);
        Assert.Equal(100, KeyframeGraphMath.YForValue(0.0, 0.0, 2.0, 100), 6);
        double y = KeyframeGraphMath.YForValue(1.5, 0.0, 2.0, 100);
        Assert.Equal(1.5, KeyframeGraphMath.ValueForY(y, 0.0, 2.0, 100), 6);
    }

    [Fact]
    public void YForValue_Clamps_Outside_The_Range_And_Centres_A_Degenerate_Range()
    {
        Assert.Equal(0, KeyframeGraphMath.YForValue(99, 0, 1, 100), 6);   // above max → top
        Assert.Equal(100, KeyframeGraphMath.YForValue(-99, 0, 1, 100), 6); // below min → bottom
        Assert.Equal(50, KeyframeGraphMath.YForValue(5, 1, 1, 100), 6);   // min==max → centred
    }

    [Fact]
    public void Progress_And_Value_Conversions_Are_Inverse_And_Guard_Flat_Segments()
    {
        Assert.Equal(0.5, KeyframeGraphMath.ValueForProgress(0.5, 0.0, 1.0), 6);
        Assert.Equal(0.25, KeyframeGraphMath.ProgressForValue(0.5, 0.0, 2.0, fallback: -1), 6);
        // Flat segment (equal endpoints): progress is undefined → returns the fallback.
        Assert.Equal(-1, KeyframeGraphMath.ProgressForValue(5, 3, 3, fallback: -1), 6);
    }

    // ── KeyframeLaneMath ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void XAt_And_TicksAt_Round_Trip_Within_The_Range()
    {
        long start = Timecode.FromSeconds(2).Ticks;
        long end = Timecode.FromSeconds(6).Ticks;
        long mid = Timecode.FromSeconds(4).Ticks;

        double x = KeyframeLaneMath.XAt(mid, start, end, laneWidth: 200);
        Assert.Equal(100, x, 3); // halfway across a 200px lane
        Assert.Equal(mid, KeyframeLaneMath.TicksAt(x, start, end, 200));
    }

    [Fact]
    public void XAt_Clamps_Outside_The_Range()
    {
        long start = Timecode.FromSeconds(2).Ticks;
        long end = Timecode.FromSeconds(6).Ticks;
        Assert.Equal(0, KeyframeLaneMath.XAt(Timecode.FromSeconds(0).Ticks, start, end, 200), 3);
        Assert.Equal(200, KeyframeLaneMath.XAt(Timecode.FromSeconds(99).Ticks, start, end, 200), 3);
    }

    [Fact]
    public void NearestKeyframeIndex_Picks_Within_Tolerance_Else_Misses()
    {
        long start = Timecode.FromSeconds(0).Ticks;
        long end = Timecode.FromSeconds(2).Ticks;
        var keys = ThreeKeys().Keyframes; // at 0s, 1s, 2s → x = 0, 100, 200 on a 200px lane

        Assert.Equal(1, KeyframeLaneMath.NearestKeyframeIndex(103, keys, start, end, 200, tolerancePx: 6));
        Assert.Equal(-1, KeyframeLaneMath.NearestKeyframeIndex(60, keys, start, end, 200, tolerancePx: 6));
    }
}
