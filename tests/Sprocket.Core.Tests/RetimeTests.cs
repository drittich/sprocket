using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Retime / speed controls (PLAN.md step 21): the clip's timeline duration and timeline→source map derive from
/// the constant <see cref="Clip.SpeedRatio"/>, and <see cref="SetClipSpeedCommand"/> applies/reverts/coalesces.
/// </summary>
public class RetimeTests
{
    private static Clip ClipFor(Rational speed)
    {
        // Source span [0, 10s) placed at t = 4s.
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.FromSeconds(4));
        clip.SpeedRatio = speed;
        return clip;
    }

    [Fact]
    public void Default_Speed_Is_Unity_And_Behaves_Identically()
    {
        Clip clip = ClipFor(Rational.One);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
        // 1× map is the plain SourceIn + (t - start).
        Assert.Equal(Timecode.FromSeconds(3), clip.MapToSource(Timecode.FromSeconds(7)));
    }

    [Fact]
    public void Double_Speed_Halves_Duration_And_Walks_Source_Twice_As_Fast()
    {
        Clip clip = ClipFor(new Rational(2, 1));
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);              // 10s source plays in 5s
        Assert.Equal(Timecode.FromSeconds(9), clip.TimelineEnd);           // start 4 + duration 5
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(5))); // 1s in → 2s of source
        // The clip end maps to the source out-point (within tick rounding).
        Assert.Equal(Timecode.FromSeconds(10), clip.MapToSource(clip.TimelineEnd));
    }

    [Fact]
    public void Half_Speed_Doubles_Duration()
    {
        Clip clip = ClipFor(new Rational(1, 2));
        Assert.Equal(Timecode.FromSeconds(20), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(8))); // 4s in → 2s of source
    }

    [Fact]
    public void SpeedRatio_Must_Be_Positive()
    {
        Clip clip = ClipFor(Rational.One);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SpeedRatio = new Rational(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SpeedRatio = new Rational(-2, 1));
    }

    [Fact]
    public void Timecode_Scale_Rounds_To_Nearest_Tick()
    {
        // 1 tick × 1/2 rounds to 1 (ties away from zero).
        Assert.Equal(1, new Timecode(1).Scale(new Rational(1, 2)).Ticks);
        Assert.Equal(6, new Timecode(3).Scale(new Rational(2, 1)).Ticks);
    }

    [Fact]
    public void SetClipSpeedCommand_Applies_And_Reverts()
    {
        Clip clip = ClipFor(Rational.One);
        var history = new EditHistory();

        history.Execute(new SetClipSpeedCommand(clip, new Rational(2, 1)));
        Assert.Equal(new Rational(2, 1), clip.SpeedRatio);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);

        history.Undo();
        Assert.Equal(Rational.One, clip.SpeedRatio);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void SetClipSpeedCommand_Coalesces_Within_A_Scope()
    {
        Clip clip = ClipFor(Rational.One);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetClipSpeedCommand(clip, new Rational(3, 2)));
            history.Execute(new SetClipSpeedCommand(clip, new Rational(2, 1)));
        }

        Assert.Equal(1, history.UndoCount);          // one undo entry for the whole gesture
        history.Undo();
        Assert.Equal(Rational.One, clip.SpeedRatio);  // reverts straight to the pre-gesture value
    }

    [Fact]
    public void Split_Preserves_Speed_On_Both_Halves()
    {
        var track = new VideoTrack();
        Clip clip = ClipFor(new Rational(2, 1)); // duration 5s, timeline [4, 9)
        track.Clips.Add(clip);
        var history = new EditHistory();

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(6)); // 2s into the clip
        history.Execute(split);

        Assert.Equal(new Rational(2, 1), clip.SpeedRatio);
        Assert.Equal(new Rational(2, 1), split.RightClip.SpeedRatio);
        // The two halves still sum to the original timeline span.
        Assert.Equal(Timecode.FromSeconds(9), split.RightClip.TimelineEnd);
    }
}
