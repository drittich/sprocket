using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Round-trip tests for multicam sources + multicam clips (PLAN.md step 24). The multicam wire shape is additive
/// and orthogonal to the sequence shape: a project with no multicam sources serializes byte-identically to a
/// pre-step-24 file (no schema bump), while a project with a synced source writes the angle list and the clip's
/// active angle.
/// </summary>
public class MulticamPersistenceTests
{
    private static Project RoundTrip(Project project)
        => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    /// <summary>A project with a two-angle multicam source (Cam 2 has a sync offset and a separate audio source)
    /// and a linked video+audio multicam clip referencing it.</summary>
    private static Project MulticamProject(out MulticamId sourceId, out MediaRefId cam2Audio)
    {
        var camA = MediaRefId.New();
        var camB = MediaRefId.New();
        cam2Audio = MediaRefId.New();
        var source = new MulticamSource(MulticamId.New(), "Multicam 1",
        [
            new MulticamAngle("Cam 1", camA),
            new MulticamAngle("Cam 2", camB, Timecode.FromSeconds(1.5), cam2Audio),
        ]);
        sourceId = source.Id;

        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.MulticamSources.Add(source);

        var link = Guid.NewGuid();
        var v = new VideoTrack { Name = "V1" };
        var vc = Clip.CreateMulticamClip(source.Id, activeAngle: 1, Timecode.FromSeconds(10), Timecode.FromSeconds(2));
        vc.LinkGroupId = link;
        v.Clips.Add(vc);
        var a = new AudioTrack { Name = "A1" };
        var ac = Clip.CreateMulticamClip(source.Id, activeAngle: 1, Timecode.FromSeconds(10), Timecode.FromSeconds(2));
        ac.LinkGroupId = link;
        a.Clips.Add(ac);
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        return project;
    }

    [Fact]
    public void Multicam_Source_And_Clip_Round_Trip()
    {
        Project loaded = RoundTrip(MulticamProject(out MulticamId sourceId, out MediaRefId cam2Audio));

        // The source comes back with both angles, names, offsets, and the separate audio source.
        MulticamSource source = Assert.Single(loaded.MulticamSources);
        Assert.Equal(sourceId, source.Id);
        Assert.Equal("Multicam 1", source.Name);
        Assert.Equal(2, source.Angles.Count);
        Assert.Equal("Cam 2", source.Angles[1].Name);
        Assert.Equal(Timecode.FromSeconds(1.5), source.Angles[1].SyncOffset);
        Assert.Equal(cam2Audio, source.Angles[1].AudioMediaRefId);
        Assert.Null(source.Angles[0].AudioMediaRefId); // Cam 1's audio comes from its video file

        // The multicam clip resolves the same source and active angle.
        Clip clip = loaded.Timeline.VideoTracks.First().Clips.Single();
        Assert.Equal(ClipKind.Multicam, clip.Kind);
        Assert.Equal(sourceId, clip.SourceMulticamId);
        Assert.Equal(1, clip.ActiveAngle);
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void Multicam_Free_Project_Omits_The_Field()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.Timeline.Tracks.Add(new VideoTrack { Name = "V1" });

        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("\"multicamSources\"", json);
        Assert.DoesNotContain("\"sourceMulticamId\"", json);
        Assert.DoesNotContain("\"activeAngle\"", json);
    }

    [Fact]
    public void Multicam_Project_Writes_The_Multicam_Shape()
    {
        string json = ProjectSerializer.Serialize(MulticamProject(out _, out _));
        Assert.Contains("\"multicamSources\"", json);
        Assert.Contains("\"sourceMulticamId\"", json);
        Assert.Contains("\"activeAngle\"", json);
        Assert.Contains("\"syncOffsetTicks\"", json);
    }
}
