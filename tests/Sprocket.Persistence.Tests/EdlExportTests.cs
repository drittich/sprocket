using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Persistence.Interchange;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>CMX3600 EDL export of the active sequence (PLAN.md step 28), including its lossy-conversion reporting.</summary>
public class EdlExportTests
{
    private static readonly MediaRefId VideoId = MediaRefId.New();
    private static readonly MediaRefId AudioId = MediaRefId.New();

    private static readonly string VideoPath = Path.Combine(Path.GetTempPath(), "clip1.mp4");
    private static readonly string AudioPath = Path.Combine(Path.GetTempPath(), "music.wav");

    private static Project BuildCut(Rational fps)
    {
        var timeline = new Timeline(fps, new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.ActiveSequence.Name = "My Cut";
        project.MediaPool.Add(new MediaRef(VideoId, VideoPath,
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, fps, 1920, 1080, false, 0, 0)));
        project.MediaPool.Add(new MediaRef(AudioId, AudioPath,
            new ProbedMediaInfo(Timecode.FromSeconds(10), false, Rational.Zero, 0, 0, true, 48000, 2)));

        var video = new VideoTrack { Name = "V1" };
        video.Clips.Add(new Clip(VideoId, Timecode.FromSeconds(1), Timecode.FromSeconds(3), Timecode.Zero));
        video.Clips.Add(new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(2)));
        var audio = new AudioTrack { Name = "A1" };
        audio.Clips.Add(new Clip(AudioId, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero));

        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        return project;
    }

    [Fact]
    public void Emits_Title_And_Non_Drop_Fcm_Header()
    {
        string edl = EdlExporter.Export(BuildCut(new Rational(30, 1))).Text;
        Assert.Contains("TITLE: My Cut", edl);
        Assert.Contains("FCM: NON-DROP FRAME", edl);
    }

    [Fact]
    public void Emits_Drop_Frame_Fcm_For_2997()
    {
        string edl = EdlExporter.Export(BuildCut(new Rational(30000, 1001))).Text;
        Assert.Contains("FCM: DROP FRAME", edl);
    }

    [Fact]
    public void Emits_One_Numbered_Event_Per_Clip_With_Source_And_Record_Timecodes()
    {
        string edl = EdlExporter.Export(BuildCut(new Rational(30, 1))).Text;
        string[] lines = edl.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Three clips → events 001, 002, 003, record-ordered (V@0, A@0, V@2s).
        Assert.Contains(lines, l => l.StartsWith("001 "));
        Assert.Contains(lines, l => l.StartsWith("002 "));
        Assert.Contains(lines, l => l.StartsWith("003 "));
        Assert.DoesNotContain(lines, l => l.StartsWith("004 "));

        // Event 001 is the first video clip: source 00:00:01:00–00:00:03:00, record 00:00:00:00–00:00:02:00.
        string first = lines.Single(l => l.StartsWith("001 "));
        Assert.Contains(" V ", first);
        Assert.Contains("00:00:01:00 00:00:03:00 00:00:00:00 00:00:02:00", first);

        // Clip names travel in the comment lines.
        Assert.Contains("* FROM CLIP NAME: clip1.mp4", edl);
        Assert.Contains("* FROM CLIP NAME: music.wav", edl);
    }

    [Fact]
    public void Audio_Clip_Uses_The_A_Channel()
    {
        string edl = EdlExporter.Export(BuildCut(new Rational(30, 1))).Text;
        string audioEvent = edl.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 3 && char.IsDigit(l[0]))
            .Single(l => l.Contains(" A "));
        Assert.StartsWith("002 ", audioEvent);
    }

    [Fact]
    public void Reports_Dropped_Effects_And_Extra_Video_Tracks()
    {
        Project project = BuildCut(new Rational(30, 1));
        project.Timeline.VideoTracks.First().Clips[0].Effects.Add(
            new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        // A second video track — EDL can only carry one.
        var v2 = new VideoTrack { Name = "V2" };
        v2.Clips.Add(new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero));
        project.Timeline.Tracks.Add(v2);

        InterchangeReport report = EdlExporter.Export(project).Report;
        Assert.True(report.HasWarnings);
        Assert.Contains(report.Warnings, w => w.Contains("effect", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, w => w.Contains("video track", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_Writes_A_File()
    {
        string path = Path.Combine(Path.GetTempPath(), "sprocket-edl-" + Guid.NewGuid().ToString("N") + ".edl");
        try
        {
            InterchangeReport report = EdlExporter.Save(BuildCut(new Rational(30, 1)), path);
            Assert.True(File.Exists(path));
            Assert.Contains("TITLE: My Cut", File.ReadAllText(path));
            Assert.NotNull(report);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
