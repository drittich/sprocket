using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Persistence.Interchange;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Final Cut Pro 7 XML (xmeml) export + import round-trip (PLAN.md step 28) — the cut travels (sequence rate /
/// resolution / name, track layout, clip placement + source in/out, file references), and unrepresentable model
/// state is reported. Uses 29.97 (NTSC) with frame-aligned clips so the frame-based round-trip is exact.
/// </summary>
public class FinalCutXmlInterchangeTests
{
    private static readonly Rational Fps = new(30000, 1001);
    private static readonly MediaRefId VideoId = MediaRefId.New();
    private static readonly MediaRefId AudioId = MediaRefId.New();
    private static readonly string VideoPath = Path.Combine(Path.GetTempPath(), "shot.mov");
    private static readonly string AudioPath = Path.Combine(Path.GetTempPath(), "score.wav");

    private static Project BuildProject()
    {
        var timeline = new Timeline(Fps, new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.ActiveSequence.Name = "Round Trip";

        project.MediaPool.Add(new MediaRef(VideoId, VideoPath,
            new ProbedMediaInfo(Timecode.FromFrames(600, Fps), true, Fps, 1920, 1080, false, 0, 0)));
        project.MediaPool.Add(new MediaRef(AudioId, AudioPath,
            new ProbedMediaInfo(Timecode.FromFrames(900, Fps), false, Rational.Zero, 0, 0, true, 48000, 2)));

        var video = new VideoTrack { Name = "V1", Enabled = true };
        video.Clips.Add(new Clip(VideoId, Timecode.FromFrames(30, Fps), Timecode.FromFrames(90, Fps), Timecode.FromFrames(0, Fps)));
        video.Clips.Add(new Clip(VideoId, Timecode.FromFrames(120, Fps), Timecode.FromFrames(150, Fps), Timecode.FromFrames(90, Fps)));

        var audio = new AudioTrack { Name = "A1", Enabled = true };
        audio.Clips.Add(new Clip(AudioId, Timecode.FromFrames(0, Fps), Timecode.FromFrames(150, Fps), Timecode.FromFrames(0, Fps)));

        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        return project;
    }

    private static Project RoundTrip(Project project)
        => FinalCutXmlInterchange.Import(FinalCutXmlInterchange.Export(project).Text).Project;

    [Fact]
    public void Round_Trips_Sequence_Format_And_Name()
    {
        Project loaded = RoundTrip(BuildProject());
        Assert.Equal("Round Trip", loaded.ActiveSequence.Name);
        Assert.Equal(Fps, loaded.Timeline.FrameRate);
        Assert.Equal(new Resolution(1920, 1080), loaded.Timeline.Resolution);
        Assert.Equal(48000, loaded.Timeline.SampleRate);
    }

    [Fact]
    public void Round_Trips_Track_Layout()
    {
        Project loaded = RoundTrip(BuildProject());
        Assert.Single(loaded.Timeline.VideoTracks);
        Assert.Single(loaded.Timeline.AudioTracks);
    }

    [Fact]
    public void Round_Trips_Clip_Placement_And_Source_In_Out()
    {
        Project loaded = RoundTrip(BuildProject());
        var clips = loaded.Timeline.VideoTracks.First().Clips;
        Assert.Equal(2, clips.Count);

        Clip a = clips[0];
        Assert.Equal(Timecode.FromFrames(30, Fps).Ticks, a.SourceIn.Ticks);
        Assert.Equal(Timecode.FromFrames(90, Fps).Ticks, a.SourceOut.Ticks);
        Assert.Equal(Timecode.FromFrames(0, Fps).Ticks, a.TimelineStart.Ticks);

        Clip b = clips[1];
        Assert.Equal(Timecode.FromFrames(120, Fps).Ticks, b.SourceIn.Ticks);
        Assert.Equal(Timecode.FromFrames(150, Fps).Ticks, b.SourceOut.Ticks);
        Assert.Equal(Timecode.FromFrames(90, Fps).Ticks, b.TimelineStart.Ticks);
    }

    [Fact]
    public void Round_Trips_The_Media_Pool_By_Id_And_Path()
    {
        Project loaded = RoundTrip(BuildProject());

        MediaRef? video = loaded.MediaPool.Get(VideoId);
        Assert.NotNull(video);
        Assert.Equal(VideoPath, video!.AbsolutePath);
        Assert.True(video.Info.HasVideo);
        Assert.Equal(1920, video.Info.Width);
        Assert.Equal(1080, video.Info.Height);

        MediaRef? audio = loaded.MediaPool.Get(AudioId);
        Assert.NotNull(audio);
        Assert.Equal(AudioPath, audio!.AbsolutePath);
        Assert.True(audio.Info.HasAudio);
        Assert.Equal(48000, audio.Info.SampleRate);
        Assert.Equal(2, audio.Info.Channels);
    }

    [Fact]
    public void The_Same_File_Used_Twice_Is_Defined_Once_And_Referenced_By_Id()
    {
        // Both video clips reference the same source; xmeml defines the <file> in full on first use and references
        // it by id afterwards — and the pool still resolves to a single source.
        string xml = FinalCutXmlInterchange.Export(BuildProject()).Text;
        int fullDefs = System.Text.RegularExpressions.Regex.Matches(xml, "<pathurl>").Count;
        Assert.Equal(2, fullDefs); // one per distinct source (video + audio), not per clip use

        Project loaded = FinalCutXmlInterchange.Import(xml).Project;
        Assert.Equal(2, loaded.MediaPool.Items.Count);
    }

    [Fact]
    public void Reports_Unrepresentable_State_On_Export()
    {
        Project project = BuildProject();
        project.Timeline.VideoTracks.First().Clips[0].Effects.Add(
            new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.3));
        project.Timeline.Markers.Add(new Marker(Timecode.FromFrames(10, Fps), "note", "", MarkerColor.Blue, Timecode.Zero));

        InterchangeReport report = FinalCutXmlInterchange.Export(project).Report;
        Assert.True(report.HasWarnings);
        Assert.Contains(report.Warnings, w => w.Contains("effect", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w => w.Contains("marker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_Rejects_Non_Xmeml()
    {
        Assert.Throws<InvalidDataException>(() => FinalCutXmlInterchange.Import("<other><thing/></other>"));
    }

    [Fact]
    public void Save_And_Load_Round_Trip_Through_A_File()
    {
        string path = Path.Combine(Path.GetTempPath(), "sprocket-fcpxml-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            FinalCutXmlInterchange.Save(BuildProject(), path);
            Assert.True(File.Exists(path));
            Project loaded = FinalCutXmlInterchange.Load(path).Project;
            Assert.Equal("Round Trip", loaded.ActiveSequence.Name);
            Assert.Equal(2, loaded.Timeline.VideoTracks.First().Clips.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
