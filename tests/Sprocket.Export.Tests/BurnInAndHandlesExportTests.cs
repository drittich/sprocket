using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Real encode→decode tests for the burn-ins &amp; handles strand of PLAN.md step 29. Burn-ins are asserted by
/// <em>localisation</em> — the corner carrying the overlay changes far more than the opposite corner — rather than
/// by exact glyphs, so the test is independent of the platform font. Handles are asserted by frame count (extra
/// frames around an in-out range) plus the pure <see cref="ExportRange.WithHandles"/> math.
/// </summary>
public sealed class BurnInAndHandlesExportTests
{
    // A generous top-left / bottom-right pair of corner regions to compare (as frame fractions).
    private const double CornerX = 0.42, CornerY = 0.22;

    [Fact]
    public void BurnIn_LightsUpTheCornerItSitsIn_AndLeavesTheRestBlack()
    {
        // A pure-black matte makes the burn-in unambiguous and font-independent: white text over black raises the
        // corner it sits in from ~0, while the rest of the frame stays black. (Over busy content the change is real
        // but small; black isolates it cleanly.)
        Project project = BlackMatteProject();

        using var plain = new TempFile();
        using var burned = new TempFile();

        VideoExporter.Export(project, plain.Path);
        var options = new ExportOptions(BurnIns: [new BurnIn(BurnInField.Timecode, BurnInPosition.TopLeft)]);
        VideoExporter.Export(project, burned.Path, options, sequenceId: null, range: null);

        double plainTopLeft = ExportProbe.FirstFrameRegionMeanRgb(plain.Path, 0, 0, CornerX, CornerY);
        double burnedTopLeft = ExportProbe.FirstFrameRegionMeanRgb(burned.Path, 0, 0, CornerX, CornerY);
        double burnedBottomRight = ExportProbe.FirstFrameRegionMeanRgb(burned.Path, 1 - CornerX, 1 - CornerY, 1, 1);

        Assert.True(burnedTopLeft > plainTopLeft + 1.0,
            $"the top-left timecode burn-in should brighten that corner: plain={plainTopLeft:0.00}, burned={burnedTopLeft:0.00}");
        Assert.True(burnedTopLeft > burnedBottomRight + 1.0,
            $"the burn-in should be localised to its corner: topLeft={burnedTopLeft:0.00}, bottomRight={burnedBottomRight:0.00}");
    }

    /// <summary>A one-video-track project whose only clip is a full-frame black colour matte over [0, 1 s).</summary>
    private static Project BlackMatteProject()
    {
        var timeline = new Timeline(new Rational(ExportFixture.Fps, 1),
            new Resolution(ExportFixture.Width, ExportFixture.Height), ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF000000");
        track.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(1), Timecode.Zero));
        timeline.Tracks.Add(track);
        return new Project(timeline);
    }

    [Fact]
    public void Handles_ExtendAnInOutRange_WithoutReachingPastTheTimeline()
    {
        Project project = ExportFixture.BuildProject(withAudio: false); // 1 s / 30 fps fixture

        using var noHandles = new TempFile();
        using var withHandles = new TempFile();
        using var whole = new TempFile();

        var range = new ExportRange(Timecode.FromSeconds(0.3), Timecode.FromSeconds(0.7)); // ~12 frames
        VideoExporter.Export(project, noHandles.Path, default, sequenceId: null, range: range);
        VideoExporter.Export(project, withHandles.Path, new ExportOptions(HandleFrames: 6), sequenceId: null, range: range);
        VideoExporter.Export(project, whole.Path); // 30 frames

        int baseFrames = ExportProbe.CountVideoFrames(noHandles.Path);
        int handleFrames = ExportProbe.CountVideoFrames(withHandles.Path);
        int wholeFrames = ExportProbe.CountVideoFrames(whole.Path);

        Assert.True(handleFrames > baseFrames,
            $"handles should add frames around the range: withHandles={handleFrames}, noHandles={baseFrames}");
        Assert.True(handleFrames < wholeFrames,
            $"a handled sub-range is still shorter than the whole timeline: withHandles={handleFrames}, whole={wholeFrames}");
    }

    [Fact]
    public void Handles_OnAWholeTimelineExport_AddNothing()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var whole = new TempFile();
        using var wholeHandles = new TempFile();
        VideoExporter.Export(project, whole.Path);
        VideoExporter.Export(project, wholeHandles.Path, new ExportOptions(HandleFrames: 10), sequenceId: null, range: null);

        // There is nothing beyond [0, duration) to extend into, so handles are clamped away.
        Assert.Equal(ExportProbe.CountVideoFrames(whole.Path), ExportProbe.CountVideoFrames(wholeHandles.Path));
    }

    [Fact]
    public void ExportRange_WithHandles_GrowsBothEnds_AndTreatsNegativesAsZero()
    {
        var range = new ExportRange(Timecode.FromSeconds(2), Timecode.FromSeconds(4));
        ExportRange grown = range.WithHandles(Timecode.FromSeconds(1), Timecode.FromSeconds(0.5));
        Assert.Equal(Timecode.FromSeconds(1).Ticks, grown.In.Ticks);
        Assert.Equal(Timecode.FromSeconds(4.5).Ticks, grown.Out.Ticks);

        // Negative handles never shrink the range.
        ExportRange unchanged = range.WithHandles(Timecode.FromSeconds(-3), Timecode.FromSeconds(-3));
        Assert.Equal(range.In.Ticks, unchanged.In.Ticks);
        Assert.Equal(range.Out.Ticks, unchanged.Out.Ticks);
    }
}
