using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Real encode→decode tests for the export queue's per-job differentiators (PLAN.md step 29): sub-range export
/// (in-out) and targeting a specific (non-active) sequence, plus a queue end-to-end run through the real
/// <see cref="VideoExporter"/> runner. The <see cref="ExportRange"/> math is checked headlessly. Together with
/// <see cref="ExportQueueTests"/> (queue mechanics, fake runner) this covers both halves of the strand.
/// </summary>
public sealed class ExportRangeTests
{
    [Fact]
    public void ExportRange_Duration_Validity_AreComputed()
    {
        var r = new ExportRange(Timecode.FromSeconds(1), Timecode.FromSeconds(3));
        Assert.Equal(Timecode.FromSeconds(2).Ticks, r.Duration.Ticks);
        Assert.True(r.IsValid);
        Assert.False(new ExportRange(Timecode.FromSeconds(2), Timecode.FromSeconds(1)).IsValid); // reversed
        Assert.False(new ExportRange(Timecode.FromSeconds(1), Timecode.FromSeconds(1)).IsValid); // empty
    }

    [Fact]
    public void ExportRange_ClampTo_PinsToTheTimelineSpan()
    {
        Timecode total = Timecode.FromSeconds(5);
        ExportRange clamped = new ExportRange(Timecode.FromSeconds(-2), Timecode.FromSeconds(9)).ClampTo(total);
        Assert.Equal(0, clamped.In.Ticks);
        Assert.Equal(total.Ticks, clamped.Out.Ticks);
    }

    [Fact]
    public void Export_SubRange_ProducesAShorterFileThanTheWhole()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var full = new TempFile();
        using var slice = new TempFile();
        VideoExporter.Export(project, full.Path);
        // [0.3s, 0.7s) of the 1 s / 30 fps fixture ≈ 12 frames, starting the file at zero.
        var range = new ExportRange(Timecode.FromSeconds(0.3), Timecode.FromSeconds(0.7));
        VideoExporter.Export(project, slice.Path, default, sequenceId: null, range: range);

        int fullFrames = ExportProbe.CountVideoFrames(full.Path);
        int sliceFrames = ExportProbe.CountVideoFrames(slice.Path);
        Assert.InRange(fullFrames, 28, 32);
        Assert.InRange(sliceFrames, 10, 14);
        Assert.True(sliceFrames < fullFrames, $"slice ({sliceFrames}) should be shorter than full ({fullFrames})");

        using MediaSource decoded = MediaSource.Open(slice.Path, HardwareAccelMode.Disabled);
        Assert.True(decoded.Info.HasAudio);
        Assert.InRange(decoded.Info.Duration.ToSeconds(), 0.30, 0.55); // ~0.4 s ± container imprecision
    }

    [Fact]
    public void Export_EmptyRange_Throws()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);
        using var output = new TempFile();
        var empty = new ExportRange(Timecode.FromSeconds(0.5), Timecode.FromSeconds(0.5));
        Assert.Throws<ArgumentException>(() =>
            VideoExporter.Export(project, output.Path, default, sequenceId: null, range: empty));
    }

    [Fact]
    public void Export_TargetsTheGivenSequence_NotOnlyTheActiveOne()
    {
        (Project project, SequenceId whiteId) = BuildTwoSequenceProject();

        using var white = new TempFile();
        using var black = new TempFile();
        VideoExporter.Export(project, white.Path, default, sequenceId: whiteId, range: null); // the NON-active sequence
        VideoExporter.Export(project, black.Path, default, sequenceId: null, range: null);    // the active sequence

        double whiteMean = ExportProbe.FirstFrameMeanRgb(white.Path);
        double blackMean = ExportProbe.FirstFrameMeanRgb(black.Path);
        Assert.True(whiteMean > blackMean + 120,
            $"Exporting the white sequence by id should be far brighter than the active black one: white={whiteMean:0.0}, black={blackMean:0.0}");
    }

    [Fact]
    public void Export_UnknownSequenceId_Throws()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);
        using var output = new TempFile();
        Assert.Throws<ArgumentException>(() =>
            VideoExporter.Export(project, output.Path, default, sequenceId: SequenceId.New(), range: null));
    }

    [Fact]
    public async Task ExportQueue_RunsRealJobsSequentially_WritingEachFile()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var whole = new TempFile();
        using var slice = new TempFile();
        var queue = new ExportQueue((job, progress, ct) =>
            VideoExporter.Export(project, job.OutputPath, job.Options, job.SequenceId, job.Range, progress, ct));

        ExportJob wholeJob = queue.Enqueue(whole.Path, default, name: "whole");
        var range = new ExportRange(Timecode.FromSeconds(0.2), Timecode.FromSeconds(0.6));
        ExportJob sliceJob = queue.Enqueue(slice.Path, default, range: range, name: "slice");

        await queue.RunAsync().WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(ExportJobStatus.Succeeded, wholeJob.Status);
        Assert.Equal(ExportJobStatus.Succeeded, sliceJob.Status);
        Assert.Equal(1.0, wholeJob.Progress);
        Assert.True(File.Exists(whole.Path));
        Assert.True(File.Exists(slice.Path));
        Assert.True(ExportProbe.CountVideoFrames(whole.Path) > ExportProbe.CountVideoFrames(slice.Path),
            "the whole-timeline job should render more frames than the sub-range job");
    }

    /// <summary>A project whose active sequence is a black colour matte and whose second sequence is a white one;
    /// returns the (non-active) white sequence's id so a test can export it by id.</summary>
    private static (Project project, SequenceId whiteId) BuildTwoSequenceProject()
    {
        var fps = new Rational(ExportFixture.Fps, 1);
        var res = new Resolution(ExportFixture.Width, ExportFixture.Height);

        Sequence black = MatteSequence("Black", "#FF000000", fps, res);
        Sequence white = MatteSequence("White", "#FFFFFFFF", fps, res);

        var project = new Project(black); // active = black
        project.Sequences.Add(white);
        return (project, white.Id);
    }

    private static Sequence MatteSequence(string name, string colorHex, Rational fps, Resolution res)
    {
        var timeline = new Timeline(fps, res, ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, colorHex);
        track.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(1), Timecode.Zero));
        timeline.Tracks.Add(track);
        return new Sequence(SequenceId.New(), name, timeline);
    }
}
