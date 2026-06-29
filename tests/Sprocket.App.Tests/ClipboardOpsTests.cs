using System;
using System.Linq;
using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ClipboardOps"/> (PLAN.md step 16c): the cut/copy/paste clip clipboard and the
/// nudge clamp. The menu wiring + pointer interaction rest on these + manual verification (the App is a UI-bound
/// WinExe).
/// </summary>
public class ClipboardOpsTests
{
    private static Clip MakeClip(double startSeconds = 2, double durationSeconds = 3)
    {
        var clip = new Clip(
            MediaRefId.New(),
            Timecode.FromSeconds(1),
            Timecode.FromSeconds(1 + durationSeconds),
            Timecode.FromSeconds(startSeconds))
        {
            LinkGroupId = Guid.NewGuid(), // the original is linked; a copy must not be
        };
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.4));
        return clip;
    }

    [Fact]
    public void Copy_Clones_Effects_And_Clears_The_Link()
    {
        Clip original = MakeClip();

        Clip copy = ClipboardOps.Copy(original);

        Assert.Equal(original.MediaRefId, copy.MediaRefId);
        Assert.Equal(original.SourceIn, copy.SourceIn);
        Assert.Equal(original.SourceOut, copy.SourceOut);
        Assert.Null(copy.LinkGroupId);                       // independent copy
        Assert.Single(copy.Effects);
        Assert.NotSame(original.Effects[0], copy.Effects[0]); // effect instance cloned, not shared
        Assert.Equal(original.Effects[0].EffectTypeId, copy.Effects[0].EffectTypeId);
    }

    [Fact]
    public void Copy_Is_Insulated_From_Later_Edits_To_The_Original()
    {
        Clip original = MakeClip();
        Clip copy = ClipboardOps.Copy(original);

        original.Effects.Add(new EffectInstance(EffectTypeIds.Fade)); // mutate after copying

        Assert.Single(copy.Effects); // the clipboard snapshot is unaffected
    }

    [Fact]
    public void Paste_Places_At_The_Given_Time_And_Clones_Effects()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Timecode at = Timecode.FromSeconds(7);

        Clip pasted = ClipboardOps.Paste(snapshot, at);

        Assert.Equal(at, pasted.TimelineStart);
        Assert.Equal(snapshot.Duration, pasted.Duration); // span preserved
        Assert.Null(pasted.LinkGroupId);
        Assert.NotSame(snapshot.Effects[0], pasted.Effects[0]);
    }

    [Fact]
    public void Paste_Clamps_A_Negative_Time_To_The_Origin()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Clip pasted = ClipboardOps.Paste(snapshot, new Timecode(-500));
        Assert.Equal(0, pasted.TimelineStart.Ticks);
    }

    [Fact]
    public void Repeated_Pastes_Are_Independent()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Clip a = ClipboardOps.Paste(snapshot, Timecode.FromSeconds(1));
        Clip b = ClipboardOps.Paste(snapshot, Timecode.FromSeconds(5));
        Assert.NotSame(a.Effects[0], b.Effects[0]);
    }

    [Theory]
    [InlineData(1000, 5000, 1000)]   // right nudge: unaffected
    [InlineData(-1000, 5000, -1000)] // left nudge with headroom: unaffected
    [InlineData(-5000, 3000, -3000)] // left nudge beyond the group origin: clamped to -minStart
    [InlineData(-1000, 0, 0)]        // group already at t=0: a left nudge is squashed to zero
    public void ClampGroupNudge_Keeps_The_Group_Off_The_Negative_Axis(long delta, long groupMin, long expected)
    {
        Assert.Equal(expected, ClipboardOps.ClampGroupNudge(delta, groupMin));
    }
}
