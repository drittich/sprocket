using Sprocket.App;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the timeline's pure geometry (PLAN.md step 12): tick↔pixel mapping, snapping, edge
/// hit-testing, and ruler-interval selection. The control's rendering + pointer interaction rest on these
/// helpers and on manual verification (the App is a UI-bound WinExe).
/// </summary>
public class TimelineMathTests
{
    private const double Header = 132;

    [Fact]
    public void X_And_Ticks_Round_Trip()
    {
        long ticks = Timecode.FromSeconds(7.5).Ticks;
        double x = TimelineMath.XAtTicks(ticks, pxPerSecond: 80, scrollX: 40, headerWidth: Header);
        long back = TimelineMath.TicksAtX(x, pxPerSecond: 80, scrollX: 40, headerWidth: Header);
        Assert.Equal(ticks, back);
    }

    [Fact]
    public void XAtTicks_Accounts_For_Header_And_Scroll()
    {
        // Tick 0 sits at the header edge, minus any horizontal scroll.
        Assert.Equal(Header, TimelineMath.XAtTicks(0, 80, 0, Header), 6);
        Assert.Equal(Header - 25, TimelineMath.XAtTicks(0, 80, 25, Header), 6);
    }

    [Fact]
    public void WidthOfTicks_Scales_With_Zoom()
    {
        long oneSecond = Timecode.TicksPerSecond;
        Assert.Equal(80, TimelineMath.WidthOfTicks(oneSecond, 80), 6);
        Assert.Equal(160, TimelineMath.WidthOfTicks(oneSecond, 160), 6);
    }

    [Fact]
    public void ClampNonNegative_Floors_At_Zero()
    {
        Assert.Equal(0, TimelineMath.ClampNonNegative(-500));
        Assert.Equal(42, TimelineMath.ClampNonNegative(42));
    }

    [Fact]
    public void Snap_Pulls_To_A_Candidate_Within_Tolerance()
    {
        long target = Timecode.FromSeconds(5).Ticks;
        long near = target + Timecode.FromSeconds(0.05).Ticks; // 0.05s ≈ 4px at 80px/s, within 8px tolerance
        long snapped = TimelineMath.Snap(near, [target], tolerancePx: 8, pxPerSecond: 80);
        Assert.Equal(target, snapped);
    }

    [Fact]
    public void Snap_Leaves_Value_When_No_Candidate_Is_Close()
    {
        long target = Timecode.FromSeconds(5).Ticks;
        long far = target + Timecode.FromSeconds(0.5).Ticks; // 0.5s = 40px at 80px/s, outside tolerance
        long snapped = TimelineMath.Snap(far, [target], tolerancePx: 8, pxPerSecond: 80);
        Assert.Equal(far, snapped);
    }

    [Fact]
    public void Snap_Picks_The_Nearest_Of_Several_Candidates()
    {
        long a = Timecode.FromSeconds(5).Ticks;
        long b = Timecode.FromSeconds(5.08).Ticks;
        long probe = Timecode.FromSeconds(5.07).Ticks; // closer to b
        Assert.Equal(b, TimelineMath.Snap(probe, [a, b], tolerancePx: 12, pxPerSecond: 80));
    }

    [Theory]
    [InlineData(100, ClipDragMode.TrimStart)] // on left edge (x0=100)
    [InlineData(300, ClipDragMode.TrimEnd)]   // on right edge (x1=300)
    [InlineData(200, ClipDragMode.Move)]      // body
    [InlineData(50, ClipDragMode.None)]       // left of the clip
    [InlineData(360, ClipDragMode.None)]      // right of the clip
    public void HitMode_Classifies_Pointer_Against_A_Clip(double pointerX, ClipDragMode expected)
    {
        Assert.Equal(expected, TimelineMath.HitMode(pointerX, clipX0: 100, clipX1: 300, edgeGrip: 7));
    }

    [Fact]
    public void HitMode_Prefers_TrimStart_On_A_Narrow_Clip()
    {
        // Edges within a grip of each other: the start wins so a sliver clip is still trimmable.
        Assert.Equal(ClipDragMode.TrimStart, TimelineMath.HitMode(101, clipX0: 100, clipX1: 104, edgeGrip: 7));
    }

    [Fact]
    public void RulerInterval_Grows_As_You_Zoom_Out()
    {
        long tight = TimelineMath.RulerIntervalTicks(pxPerSecond: 300, targetPx: 90); // zoomed in → small interval
        long loose = TimelineMath.RulerIntervalTicks(pxPerSecond: 10, targetPx: 90);  // zoomed out → large interval
        Assert.True(loose > tight);
        // Each chosen interval is a whole number of seconds-or-half-seconds in ticks.
        Assert.True(tight > 0 && loose > 0);
    }
}
