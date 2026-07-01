using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Batch relink &amp; offline recovery (PLAN.md step 28). The matcher tests are pure (no filesystem); a couple of
/// integration tests exercise the find-offline → scan-folder → apply loop against real temp files.
/// </summary>
public class MediaRelinkTests
{
    private static readonly MediaRefId A = MediaRefId.New();
    private static readonly MediaRefId B = MediaRefId.New();

    // ---- pure matcher ----

    [Fact]
    public void Matches_A_Single_Same_Named_Candidate()
    {
        var offline = new[] { new OfflineMedia(A, @"C:\old\clip.mp4") };
        var candidates = new[] { new MediaCandidate(@"D:\new\clip.mp4", 100) };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        RelinkMatch m = Assert.Single(plan.Matches);
        Assert.Equal(A, m.Id);
        Assert.Equal(@"D:\new\clip.mp4", m.NewPath);
        Assert.Empty(plan.Ambiguous);
        Assert.Empty(plan.Unmatched);
    }

    [Fact]
    public void Reports_Unmatched_When_No_Candidate_Shares_The_Name()
    {
        var offline = new[] { new OfflineMedia(A, @"C:\old\clip.mp4") };
        var candidates = new[] { new MediaCandidate(@"D:\new\other.mp4", 100) };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        Assert.Empty(plan.Matches);
        Assert.Equal(A, Assert.Single(plan.Unmatched).Id);
    }

    [Fact]
    public void Disambiguates_Multiple_Same_Named_By_Longest_Path_Tail()
    {
        // The original lived under …\media\clip.mp4; the candidate that also lives under a "media" folder wins.
        var offline = new[] { new OfflineMedia(A, @"C:\proj\media\clip.mp4") };
        var candidates = new[]
        {
            new MediaCandidate(@"D:\backup\other\clip.mp4", 100),
            new MediaCandidate(@"D:\backup\media\clip.mp4", 100),
        };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        Assert.Equal(@"D:\backup\media\clip.mp4", Assert.Single(plan.Matches).NewPath);
    }

    [Fact]
    public void Reports_Ambiguous_When_Same_Named_Candidates_Tie()
    {
        var offline = new[] { new OfflineMedia(A, @"C:\old\clip.mp4") };
        var candidates = new[]
        {
            new MediaCandidate(@"D:\one\clip.mp4", 100),
            new MediaCandidate(@"D:\two\clip.mp4", 200),
        };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        Assert.Empty(plan.Matches);
        Assert.Equal(A, Assert.Single(plan.Ambiguous).Id);
    }

    [Fact]
    public void Breaks_A_Path_Tail_Tie_By_Known_Original_Size()
    {
        var offline = new[] { new OfflineMedia(A, @"C:\old\clip.mp4") };
        var candidates = new[]
        {
            new MediaCandidate(@"D:\one\clip.mp4", 100),
            new MediaCandidate(@"D:\two\clip.mp4", 999),
        };
        var sizes = new Dictionary<MediaRefId, long> { [A] = 999 };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates, sizes);
        Assert.Equal(@"D:\two\clip.mp4", Assert.Single(plan.Matches).NewPath);
    }

    [Fact]
    public void An_Offline_Source_With_No_Path_Cannot_Be_Auto_Matched()
    {
        // A collaborator's clip that was never linked on this machine has no file name to match on — reported
        // unmatched rather than mis-linked.
        var offline = new[] { new OfflineMedia(A, string.Empty) };
        var candidates = new[] { new MediaCandidate(@"D:\new\clip.mp4", 100) };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        Assert.Empty(plan.Matches);
        Assert.Equal(A, Assert.Single(plan.Unmatched).Id);
    }

    [Fact]
    public void File_Name_Matching_Is_Case_Insensitive()
    {
        var offline = new[] { new OfflineMedia(A, @"C:\old\Clip.MP4") };
        var candidates = new[] { new MediaCandidate(@"D:\new\clip.mp4", 100) };

        RelinkPlan plan = MediaRelinkMatcher.Match(offline, candidates);
        Assert.Single(plan.Matches);
    }

    [Fact]
    public void Common_Tail_Counts_Segments_Across_Either_Separator()
    {
        // A Windows-recorded original path matches a POSIX candidate (and vice versa) so cross-OS projects relink.
        Assert.Equal(2, MediaRelinkMatcher.CommonTailSegments(@"C:\proj\media\clip.mp4", "/mnt/d/media/clip.mp4"));
        Assert.Equal(1, MediaRelinkMatcher.CommonTailSegments(@"C:\a\clip.mp4", @"C:\b\clip.mp4"));
        Assert.Equal(0, MediaRelinkMatcher.CommonTailSegments(@"C:\a\one.mp4", @"C:\a\two.mp4"));
    }

    // ---- I/O integration ----

    [Fact]
    public void Find_Offline_Flags_Missing_And_Empty_Paths_But_Not_Present_Files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-relink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string present = Path.Combine(dir, "here.mp4");
            File.WriteAllText(present, "x");

            var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
            var online = MediaRefId.New();
            var missing = MediaRefId.New();
            var offlineId = MediaRefId.New();
            project.MediaPool.Add(new MediaRef(online, present, Probe()));
            project.MediaPool.Add(new MediaRef(missing, Path.Combine(dir, "gone.mp4"), Probe()));
            project.MediaPool.Add(new MediaRef(offlineId, string.Empty, Probe()));

            IReadOnlyList<OfflineMedia> offline = MediaRelink.FindOffline(project);
            Assert.Equal(2, offline.Count);
            Assert.Contains(offline, o => o.Id == missing);
            Assert.Contains(offline, o => o.Id == offlineId);
            Assert.DoesNotContain(offline, o => o.Id == online);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_And_Apply_Relinks_A_Moved_Media_Folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "sprocket-relink-" + Guid.NewGuid().ToString("N"));
        string newFolder = Path.Combine(root, "moved");
        Directory.CreateDirectory(newFolder);
        try
        {
            string moved = Path.Combine(newFolder, "clip.mp4");
            File.WriteAllText(moved, "x");

            var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
            var id = MediaRefId.New();
            // Recorded at an old location that no longer exists (files moved into newFolder).
            project.MediaPool.Add(new MediaRef(id, Path.Combine(root, "original", "clip.mp4"), Probe()));

            RelinkPlan plan = MediaRelink.Plan(project, root);
            Assert.Equal(id, Assert.Single(plan.Matches).Id);

            int relinked = MediaRelink.Apply(project, plan);
            Assert.Equal(1, relinked);
            Assert.Equal(moved, project.MediaPool.Get(id)!.AbsolutePath);
            Assert.Empty(MediaRelink.FindOffline(project)); // now online
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProbedMediaInfo Probe() =>
        new(Timecode.FromSeconds(5), true, new Rational(30, 1), 1920, 1080, false, 0, 0);
}
