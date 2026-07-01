using System.IO;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Headless tests for the export burn-in model (PLAN.md step 29): resolving a <see cref="BurnIn"/> to the string
/// baked on the frame — timecode formatting at the sequence rate, the topmost content clip's name, and a literal
/// watermark. Pure model, no Skia / FFmpeg (the drawing is tested in Render / the real-encode export test).
/// </summary>
public sealed class BurnInTests
{
    private static readonly Rational Fps30 = new(30, 1);

    private static ProbedMediaInfo VideoInfo() =>
        new(Timecode.FromSeconds(10), HasVideo: true, Fps30, 640, 480, HasAudio: false, 0, 0);

    /// <summary>A project with one video track carrying a media clip named after <paramref name="fileName"/>
    /// over [0, 5s). Returns the project and its single sequence.</summary>
    private static (Project project, Sequence sequence) MediaProject(string fileName)
    {
        var timeline = new Timeline(Fps30, new Resolution(640, 480), 48000);
        var project = new Project(timeline);

        var id = MediaRefId.New();
        // Build the path with the platform separator so Path.GetFileName trims it on Windows and Linux alike.
        project.MediaPool.Add(new MediaRef(id, Path.Combine(Path.GetTempPath(), fileName), VideoInfo()));

        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        timeline.Tracks.Add(track);
        return (project, project.ActiveSequence);
    }

    [Fact]
    public void Timecode_Field_FormatsRecordTimeAtTheSequenceRate()
    {
        (Project project, Sequence sequence) = MediaProject("shot.mov");
        var item = new BurnIn(BurnInField.Timecode);

        Assert.Equal("00:00:00:00", BurnInResolver.Resolve(item, project, sequence, Timecode.Zero));
        Assert.Equal("00:00:01:00", BurnInResolver.Resolve(item, project, sequence, Timecode.FromSeconds(1)));
        // 45 frames at 30 fps = 1 s + 15 frames.
        Assert.Equal("00:00:01:15", BurnInResolver.Resolve(item, project, sequence, Timecode.FromFrames(45, Fps30)));
    }

    [Fact]
    public void ClipName_Field_ReturnsTheMediaFileName_OverTheClip_AndEmptyOverAGap()
    {
        (Project project, Sequence sequence) = MediaProject("interview_A.mp4");
        var item = new BurnIn(BurnInField.ClipName);

        Assert.Equal("interview_A.mp4", BurnInResolver.Resolve(item, project, sequence, Timecode.FromSeconds(2)));
        // The clip ends at 5 s; past it there is no content, so nothing is drawn.
        Assert.Equal(string.Empty, BurnInResolver.Resolve(item, project, sequence, Timecode.FromSeconds(8)));
    }

    [Fact]
    public void ClipName_Field_PrefersTheTopmostTrack()
    {
        (Project project, Sequence sequence) = MediaProject("under.mp4");

        // Add a higher video track (later in z-order = on top) with its own clip covering the same time.
        var top = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(top, Path.Combine(Path.GetTempPath(), "over.mp4"), VideoInfo()));
        var upper = new VideoTrack { Name = "V2" };
        upper.Clips.Add(new Clip(top, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        sequence.Timeline.Tracks.Add(upper);

        Assert.Equal("over.mp4", BurnInResolver.TopmostClipName(project, sequence, Timecode.FromSeconds(1)));
    }

    [Fact]
    public void ClipName_Field_SkipsAdjustmentLayers_ForTheNameAbove()
    {
        (Project project, Sequence sequence) = MediaProject("base.mp4");

        // An adjustment layer on top carries no content of its own — the name should still be the media beneath it.
        var adj = new VideoTrack { Name = "V2" };
        adj.Clips.Add(Clip.CreateAdjustment(Timecode.FromSeconds(5), Timecode.Zero));
        sequence.Timeline.Tracks.Add(adj);

        Assert.Equal("base.mp4", BurnInResolver.TopmostClipName(project, sequence, Timecode.FromSeconds(1)));
    }

    [Fact]
    public void ClipName_Field_UsesGeneratorLabels()
    {
        var timeline = new Timeline(Fps30, new Resolution(640, 480), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };

        var titled = new GeneratorSpec(GeneratorTypeIds.Title).SetString(GeneratorParamNames.Text, "Opening Card");
        track.Clips.Add(Clip.CreateGenerator(titled, Timecode.FromSeconds(2), Timecode.Zero));
        var matte = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF102030");
        track.Clips.Add(Clip.CreateGenerator(matte, Timecode.FromSeconds(2), Timecode.FromSeconds(2)));
        timeline.Tracks.Add(track);

        Sequence sequence = project.ActiveSequence;
        Assert.Equal("Opening Card", BurnInResolver.TopmostClipName(project, sequence, Timecode.FromSeconds(1)));
        Assert.Equal("Color Matte", BurnInResolver.TopmostClipName(project, sequence, Timecode.FromSeconds(3)));
    }

    [Fact]
    public void Text_Field_ReturnsTheLiteralWatermark()
    {
        (Project project, Sequence sequence) = MediaProject("shot.mov");
        var item = new BurnIn(BurnInField.Text, BurnInPosition.BottomRight, "CONFIDENTIAL");
        Assert.Equal("CONFIDENTIAL", BurnInResolver.Resolve(item, project, sequence, Timecode.FromSeconds(1)));

        // A Text burn-in with no string resolves to empty (the renderer skips it).
        var empty = new BurnIn(BurnInField.Text, BurnInPosition.BottomRight);
        Assert.Equal(string.Empty, BurnInResolver.Resolve(empty, project, sequence, Timecode.Zero));
    }
}
