using Sprocket.App.RenderCache;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Persistence;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The render-cache store + service (PLAN.md step 32): the cache dir lands beside the project, committed
/// segments resolve for playback and survive a service reload via the manifest, an invalidating edit turns a
/// segment dirty and undoing it re-validates with no re-render, Delete Render Files reclaims the disk, and
/// cached audio reads back through the WAV seam. All headless — segments point at scratch files.
/// </summary>
public class RenderCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sprocket-rendercache-{Guid.NewGuid():N}");
    private readonly string _projectPath;

    public RenderCacheServiceTests()
    {
        Directory.CreateDirectory(_root);
        _projectPath = Path.Combine(_root, "MyMovie.sprocket.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort scratch cleanup */ }
    }

    /// <summary>A media-less project (one generator video clip, 4 s) so hashing never touches the file system.</summary>
    private static Project BuildProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1280, 720), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF303030");
        track.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(4), Timecode.Zero));
        timeline.Tracks.Add(track);
        timeline.Tracks.Add(new AudioTrack { Name = "A1" });
        return project;
    }

    private static PendingRender CommitVideoSegment(RenderCacheService service, Project project, double inSec, double outSec)
    {
        PendingRender pending = service.Prepare(
            RenderCacheScope.Video, project.ActiveSequence.Id,
            Timecode.FromSeconds(inSec), Timecode.FromSeconds(outSec));
        Directory.CreateDirectory(Path.GetDirectoryName(service.FilePathFor(pending))!);
        File.WriteAllBytes(service.FilePathFor(pending), [1, 2, 3, 4]); // a stand-in intermediate
        service.Commit(pending);
        return pending;
    }

    [Fact]
    public void The_Cache_Directory_Sits_Beside_The_Project()
    {
        string dir = RenderCacheStore.DirectoryFor(_projectPath);
        Assert.Equal(Path.Combine(_root, "Sprocket Render Files", "MyMovie.sprocket"), dir);
    }

    [Fact]
    public void A_Committed_Segment_Resolves_For_Playback_And_Survives_Reload()
    {
        Project project = BuildProject();
        using (var service = new RenderCacheService(project, _projectPath))
        {
            CommitVideoSegment(service, project, 0, 2);

            CachedRenderSegment? segment = service.ResolveAt(Timecode.FromSeconds(1));
            Assert.NotNull(segment);
            Assert.True(File.Exists(segment!.Value.FilePath));
            Assert.Null(service.ResolveAt(Timecode.FromSeconds(3))); // outside the range
        }

        // A fresh service (a reopened project) validates the manifest against the unchanged model: still green.
        using var reloaded = new RenderCacheService(project, _projectPath);
        Assert.NotNull(reloaded.ResolveAt(Timecode.FromSeconds(1)));
    }

    [Fact]
    public void An_Edit_Invalidates_And_Undo_Revalidates_Without_A_Rerender()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);
        CommitVideoSegment(service, project, 0, 2);
        Assert.NotNull(service.ResolveAt(Timecode.FromSeconds(1)));

        // The edit (an effect on the overlapping clip) → dirty on refresh.
        Clip clip = project.Timeline.VideoTracks.First().Clips[0];
        var effect = new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5);
        clip.Effects.Add(effect);
        service.Refresh();
        Assert.Null(service.ResolveAt(Timecode.FromSeconds(1)));
        Assert.False(service.SegmentsForActiveSequence()[0].Valid);

        // Undo → valid again, same file, no re-render.
        clip.Effects.Remove(effect);
        service.Refresh();
        Assert.NotNull(service.ResolveAt(Timecode.FromSeconds(1)));
        Assert.True(service.SegmentsForActiveSequence()[0].Valid);
    }

    [Fact]
    public void IsAlreadyRendered_Short_Circuits_A_Repeat_Render()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);
        PendingRender pending = CommitVideoSegment(service, project, 0, 2);

        Assert.True(service.IsAlreadyRendered(pending));
        Assert.False(service.IsAlreadyRendered(service.Prepare(
            RenderCacheScope.Video, project.ActiveSequence.Id, Timecode.FromSeconds(2), Timecode.FromSeconds(4))));
    }

    [Fact]
    public void DeleteAll_Reclaims_The_Disk_And_Stops_Resolving()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);
        CommitVideoSegment(service, project, 0, 2);
        Assert.True(service.SizeBytes() > 0);

        long reclaimed = service.DeleteAll();
        Assert.True(reclaimed > 0);
        Assert.Null(service.ResolveAt(Timecode.FromSeconds(1)));
        Assert.Empty(service.SegmentsForActiveSequence());
        Assert.Equal(0, Directory.Exists(service.Directory)
            ? Directory.EnumerateFiles(service.Directory).Count(f => !f.EndsWith("manifest.json", StringComparison.Ordinal))
            : 0);
    }

    [Fact]
    public void A_Committed_Range_Supersedes_Contained_Segments()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);
        CommitVideoSegment(service, project, 1, 2);
        CommitVideoSegment(service, project, 0, 4); // fully contains the first
        Assert.Single(service.SegmentsForActiveSequence());
    }

    [Fact]
    public void Cached_Audio_Reads_Back_Through_The_Wav_Seam()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);

        PendingRender pending = service.Prepare(
            RenderCacheScope.Audio, project.ActiveSequence.Id,
            Timecode.FromSeconds(1), Timecode.FromSeconds(3), sampleRate: 48000, channels: 2);
        string path = service.FilePathFor(pending);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new WavePcmWriter(path, 48000, 2))
        {
            float[] chunk = new float[4800 * 2];
            Array.Fill(chunk, 0.5f);
            for (int i = 0; i < 20; i++) // 2 s of 0.5
                writer.Write(chunk);
            writer.Finish();
        }
        service.Commit(pending);

        // Fully inside the cached range → replayed.
        float[] buffer = new float[512 * 2];
        Assert.True(service.TryRead(Timecode.FromSeconds(2), buffer));
        Assert.All(buffer, s => Assert.Equal(0.5f, s));

        // Straddling the range end → refused (the caller mixes live; no seams inside one buffer).
        Assert.False(service.TryRead(Timecode.FromSeconds(2.999), buffer));
        // Before the range → refused.
        Assert.False(service.TryRead(Timecode.Zero, buffer));
    }

    [Fact]
    public void Video_Segments_Do_Not_Serve_Other_Sequences()
    {
        Project project = BuildProject();
        using var service = new RenderCacheService(project, _projectPath);
        CommitVideoSegment(service, project, 0, 2);

        // Switch the active sequence: the segment belongs to the old one and must stop resolving.
        var other = new Sequence(SequenceId.New(), "Other",
            new Timeline(new Rational(30, 1), new Resolution(1280, 720), 48000));
        project.Sequences.Add(other);
        project.ActiveSequence = other;
        service.Refresh();

        Assert.Null(service.ResolveAt(Timecode.FromSeconds(1)));

        // Switching back restores it.
        project.ActiveSequence = project.Sequences[0];
        service.Refresh();
        Assert.NotNull(service.ResolveAt(Timecode.FromSeconds(1)));
    }
}
