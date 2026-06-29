using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class AnimatableValueTests
{
    [Fact]
    public void Constant_Ignores_Time()
    {
        var v = AnimatableValue.Constant(0.5);
        Assert.False(v.IsAnimated);
        Assert.Equal(0.5, v.Evaluate(Timecode.Zero));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(100)));
    }

    [Fact]
    public void Linear_Interpolates_Midpoint()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(0), 1.0),
            new Keyframe(Timecode.FromSeconds(2), 0.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(0)));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 6);
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Clamps_Outside_Keyframe_Range()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(1), 0.2),
            new Keyframe(Timecode.FromSeconds(3), 0.8),
        });

        Assert.Equal(0.2, v.Evaluate(Timecode.FromSeconds(-5)));
        Assert.Equal(0.8, v.Evaluate(Timecode.FromSeconds(100)));
    }

    [Fact]
    public void Hold_Steps_Instead_Of_Interpolating()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(0), 1.0, Interpolation.Hold),
            new Keyframe(Timecode.FromSeconds(2), 0.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(1.9)));
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Keyframes_Are_Sorted_Regardless_Of_Input_Order()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(2), 0.0),
            new Keyframe(Timecode.FromSeconds(0), 1.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(0)));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 6);
    }

    [Fact]
    public void Empty_Keyframes_Throws()
    {
        Assert.Throws<ArgumentException>(() => AnimatableValue.Animated(Array.Empty<Keyframe>()));
    }

    // ── Eased interpolation (PLAN.md step 16d) ───────────────────────────────────────────────────────

    private static AnimatableValue Ramp(Interpolation mode) => AnimatableValue.Animated(new[]
    {
        new Keyframe(Timecode.FromSeconds(0), 0.0, mode),
        new Keyframe(Timecode.FromSeconds(2), 1.0),
    });

    [Theory]
    [InlineData(Interpolation.Linear)]
    [InlineData(Interpolation.EaseIn)]
    [InlineData(Interpolation.EaseOut)]
    [InlineData(Interpolation.EaseInOut)]
    public void Eased_Modes_Hit_The_Keyframe_Values_At_The_Endpoints(Interpolation mode)
    {
        var v = Ramp(mode);
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(0)), 6);
        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(2)), 6);
    }

    [Fact]
    public void EaseOut_Starts_Slow_So_Midpoint_Is_Below_Linear()
    {
        // f(x) = x²: gentle (accelerating) departure from the keyframe.
        var v = Ramp(Interpolation.EaseOut);
        Assert.Equal(0.25, v.Evaluate(Timecode.FromSeconds(1)), 6); // x=0.5 → 0.25
        Assert.True(v.Evaluate(Timecode.FromSeconds(1)) < 0.5);
    }

    [Fact]
    public void EaseIn_Starts_Fast_So_Midpoint_Is_Above_Linear()
    {
        // f(x) = 1-(1-x)²: decelerating arrival at the next keyframe.
        var v = Ramp(Interpolation.EaseIn);
        Assert.Equal(0.75, v.Evaluate(Timecode.FromSeconds(1)), 6); // x=0.5 → 0.75
        Assert.True(v.Evaluate(Timecode.FromSeconds(1)) > 0.5);
    }

    [Fact]
    public void EaseInOut_Is_Symmetric_With_Midpoint_At_Half()
    {
        // Smoothstep f(x) = x²(3-2x): zero velocity at both ends, midpoint exactly 0.5.
        var v = Ramp(Interpolation.EaseInOut);
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 6);                 // x=0.5
        double a = v.Evaluate(Timecode.FromSeconds(0.5));                          // x=0.25
        double b = v.Evaluate(Timecode.FromSeconds(1.5));                          // x=0.75
        Assert.Equal(1.0, a + b, 6);                                               // symmetric about the midpoint
    }

    [Theory]
    [InlineData(Interpolation.EaseIn)]
    [InlineData(Interpolation.EaseOut)]
    [InlineData(Interpolation.EaseInOut)]
    [InlineData(Interpolation.Bezier)]
    public void Eased_Modes_Are_Monotonic(Interpolation mode)
    {
        var v = Ramp(mode);
        double prev = -1;
        for (int i = 0; i <= 20; i++)
        {
            double now = v.Evaluate(Timecode.FromSeconds(2.0 * i / 20));
            Assert.True(now >= prev - 1e-9, $"non-monotonic at step {i}: {now} < {prev}");
            prev = now;
        }
    }

    // ── Custom Bezier velocity curves (PLAN.md step 16d, item 1) ─────────────────────────────────────

    [Fact]
    public void Bezier_With_Default_Handles_Is_A_Symmetric_Smooth_Ease()
    {
        // Bezier with null handles falls back to the gentle "easy ease" defaults — symmetric about the midpoint.
        var v = Ramp(Interpolation.Bezier);
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(0)), 6);
        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(2)), 6);
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 3);
        double a = v.Evaluate(Timecode.FromSeconds(0.5));
        double b = v.Evaluate(Timecode.FromSeconds(1.5));
        Assert.Equal(1.0, a + b, 3); // symmetric
    }

    [Fact]
    public void Bezier_With_Linear_Equivalent_Handles_Matches_Linear()
    {
        // cubic-bezier(1/3,1/3, 2/3,2/3) is the identity curve → a straight line.
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(0), 0.0, Interpolation.Bezier, EaseOut: new BezierHandle(1.0 / 3, 1.0 / 3)),
            new Keyframe(Timecode.FromSeconds(2), 1.0, EaseIn: new BezierHandle(2.0 / 3, 2.0 / 3)),
        });
        Assert.Equal(0.25, v.Evaluate(Timecode.FromSeconds(0.5)), 3);
        Assert.Equal(0.50, v.Evaluate(Timecode.FromSeconds(1.0)), 3);
        Assert.Equal(0.75, v.Evaluate(Timecode.FromSeconds(1.5)), 3);
    }
}
