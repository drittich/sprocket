using System.Linq;
using Sprocket.App;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ClipPlacement"/> (PLAN.md step 16b): snapping a dropped clip's start and
/// building the (single or linked-composite) <see cref="AddClipCommand"/> for a media-bin drag. The drag/drop
/// plumbing rests on these + manual verification (the App is a UI-bound WinExe).
/// </summary>
public class ClipPlacementTests
{
    private static MediaRef Media(bool video, bool audio, double durationSeconds = 5) =>
        new(MediaRefId.New(), "/tmp/clip.mp4",
            new ProbedMediaInfo(
                Timecode.FromSeconds(durationSeconds), video, video ? new Rational(30, 1) : Rational.Zero,
                video ? 1920 : 0, video ? 1080 : 0, audio, audio ? 48000 : 0, audio ? 2 : 0));

    private const double PxPerSecond = 80;
    private const double Tolerance = 8;

    [Fact]
    public void SnapStart_Snaps_Start_To_A_Nearby_Edge()
    {
        long target = Timecode.FromSeconds(5).Ticks;
        long near = target + Timecode.FromSeconds(0.05).Ticks; // ≈4px at 80px/s, within tolerance
        long dur = Timecode.FromSeconds(3).Ticks;
        long snapped = ClipPlacement.SnapStart(near, dur, [target], snapping: true, Tolerance, PxPerSecond);
        Assert.Equal(target, snapped);
    }

    [Fact]
    public void SnapStart_Snaps_The_Trailing_Edge_Flush()
    {
        long edge = Timecode.FromSeconds(10).Ticks;
        long dur = Timecode.FromSeconds(3).Ticks;
        // Drop so the clip END lands ≈ at the edge: start ≈ edge - dur, nudged a hair off.
        long drop = edge - dur + Timecode.FromSeconds(0.04).Ticks;
        long snapped = ClipPlacement.SnapStart(drop, dur, [edge], snapping: true, Tolerance, PxPerSecond);
        Assert.Equal(edge - dur, snapped);
    }

    [Fact]
    public void SnapStart_Clamps_To_Origin_And_Honours_Off_Switch()
    {
        Assert.Equal(0, ClipPlacement.SnapStart(-500, 100, [], snapping: true, Tolerance, PxPerSecond));
        long drop = Timecode.FromSeconds(5).Ticks;
        // Snapping off: returns the raw (clamped) start even with a candidate right there.
        Assert.Equal(drop, ClipPlacement.SnapStart(drop, 100, [drop], snapping: false, Tolerance, PxPerSecond));
    }

    [Fact]
    public void BuildPlaceCommand_AV_Source_Places_Linked_Pair()
    {
        MediaRef media = Media(video: true, audio: true);
        var v = new VideoTrack();
        var a = new AudioTrack();
        long start = Timecode.FromSeconds(2).Ticks;

        ClipPlacement.PlacementResult? result =
            ClipPlacement.BuildPlaceCommand(media, v, a, start, linked: true, primaryIsVideo: true);
        Assert.NotNull(result);

        var history = new EditHistory();
        history.Execute(result!.Value.Command);

        Clip vc = Assert.Single(v.Clips);
        Clip ac = Assert.Single(a.Clips);
        Assert.Equal(start, vc.TimelineStart.Ticks);
        Assert.Equal(media.Info.Duration, vc.SourceOut);
        Assert.NotNull(vc.LinkGroupId);
        Assert.Equal(vc.LinkGroupId, ac.LinkGroupId);     // companions share one group
        Assert.Same(vc, result.Value.PrimaryClip);         // video lane was primary

        history.Undo(); // one composite entry undoes both
        Assert.Empty(v.Clips);
        Assert.Empty(a.Clips);
    }

    [Fact]
    public void BuildPlaceCommand_VideoOnly_Source_Places_Single_Unlinked_Clip()
    {
        MediaRef media = Media(video: true, audio: false);
        var v = new VideoTrack();
        var a = new AudioTrack();

        ClipPlacement.PlacementResult? result =
            ClipPlacement.BuildPlaceCommand(media, v, a, 0, linked: true, primaryIsVideo: true);
        Assert.NotNull(result);
        new EditHistory().Execute(result!.Value.Command);

        Assert.Single(v.Clips);
        Assert.Empty(a.Clips);               // no audio stream → no audio clip
        Assert.Null(v.Clips[0].LinkGroupId); // nothing to link to
    }

    [Fact]
    public void BuildPlaceCommand_Returns_Null_When_No_Compatible_Track()
    {
        // Audio-only source dropped with only a video track available.
        MediaRef media = Media(video: false, audio: true);
        Assert.Null(ClipPlacement.BuildPlaceCommand(media, new VideoTrack(), null, 0, linked: true, primaryIsVideo: true));
    }

    [Fact]
    public void BuildPlaceCommand_Unlinked_Places_Both_Without_A_Group()
    {
        MediaRef media = Media(video: true, audio: true);
        var v = new VideoTrack();
        var a = new AudioTrack();

        ClipPlacement.PlacementResult? result =
            ClipPlacement.BuildPlaceCommand(media, v, a, 0, linked: false, primaryIsVideo: false);
        new EditHistory().Execute(result!.Value.Command);

        Assert.Null(v.Clips[0].LinkGroupId);
        Assert.Null(a.Clips[0].LinkGroupId);
        Assert.Same(a.Clips[0], result.Value.PrimaryClip); // audio lane was primary
    }

    // ── CompatibleTrack (cross-track drag target, PLAN.md step 16e) ──────────────────────────────────

    [Fact]
    public void CompatibleTrack_Returns_The_Lane_When_It_Is_The_Same_Kind()
    {
        var src = new VideoTrack();
        var lane = new VideoTrack();
        Assert.Same(lane, ClipPlacement.CompatibleTrack(src, lane));

        var asrc = new AudioTrack();
        var alane = new AudioTrack();
        Assert.Same(alane, ClipPlacement.CompatibleTrack(asrc, alane));
    }

    [Fact]
    public void CompatibleTrack_Is_Null_For_A_Cross_Kind_Or_Missing_Lane()
    {
        var video = new VideoTrack();
        var audio = new AudioTrack();
        Assert.Null(ClipPlacement.CompatibleTrack(video, audio)); // video clip can't drop on an audio lane
        Assert.Null(ClipPlacement.CompatibleTrack(audio, video));
        Assert.Null(ClipPlacement.CompatibleTrack(video, null)); // no lane under the cursor → keep source
    }
}
