using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// The collaboration-ready format split (PLAN.md step 28, ARCHITECTURE.md §12): the shared project file references
/// sources by id only, while the per-user asset paths live in a <c>.links.json</c> media-link sidecar. These verify
/// the split, its backward compatibility with self-contained / pre-step-28 files, and the offline-until-relinked
/// behaviour when a project is pulled without its sidecar.
/// </summary>
public class MediaLinkPersistenceTests
{
    private static readonly MediaRefId ClipId = MediaRefId.New();

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Project OneMediaProject(string absolutePath)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.MediaPool.Add(new MediaRef(ClipId, absolutePath,
            new ProbedMediaInfo(Timecode.FromSeconds(5), true, new Rational(30, 1), 1920, 1080, false, 0, 0)));
        return project;
    }

    [Fact]
    public void Save_Omits_Media_Paths_From_The_Project_File_And_Writes_Them_To_The_Sidecar()
    {
        string dir = NewTempDir();
        try
        {
            string media = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(media, "x");
            string projectPath = Path.Combine(dir, "project.sprocket.json");
            ProjectSerializer.Save(OneMediaProject(media), projectPath);

            // The shared project file references the source by id only — no path (diffable, merge-friendly).
            string projectJson = File.ReadAllText(projectPath);
            Assert.Contains(ClipId.Value.ToString(), projectJson);
            Assert.DoesNotContain("absolutePath", projectJson);
            Assert.DoesNotContain("clip.mp4", projectJson);

            // The per-user paths live in the sidecar beside it.
            string sidecar = MediaLinks.SidecarPath(projectPath);
            Assert.True(File.Exists(sidecar));
            Assert.Contains("clip.mp4", File.ReadAllText(sidecar));

            // Loading the pair resolves the path again.
            Project loaded = ProjectSerializer.Load(projectPath);
            Assert.Equal(Path.GetFullPath(media), Path.GetFullPath(loaded.MediaPool.Get(ClipId)!.AbsolutePath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Project_File_Shared_Without_Its_Sidecar_Loads_The_Source_Offline()
    {
        // The collaboration payoff: a collaborator who pulls only the project file (no per-user link file) gets the
        // edit without being forced to relocate clips — the source loads offline (empty path), ready to relink.
        string dir = NewTempDir();
        try
        {
            string media = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(media, "x");
            string projectPath = Path.Combine(dir, "project.sprocket.json");
            ProjectSerializer.Save(OneMediaProject(media), projectPath);

            MediaLinks.Delete(projectPath); // simulate a fresh clone with no sidecar

            Project loaded = ProjectSerializer.Load(projectPath);
            MediaRef? clip = loaded.MediaPool.Get(ClipId);
            Assert.NotNull(clip); // the source is still in the pool (renders as black/silence, §15)...
            Assert.Equal(string.Empty, clip!.AbsolutePath); // ...but offline, awaiting relink
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SelfContained_Serialize_Inlines_Paths_And_Round_Trips_Without_A_Sidecar()
    {
        // The autosave / snapshot string form is self-contained: a lone string has nowhere else to put the path,
        // so it inlines it and round-trips on its own (no sidecar involved).
        const string absolute = @"C:\media\clip.mp4";
        string json = ProjectSerializer.Serialize(OneMediaProject(absolute));
        Assert.Contains("absolutePath", json);

        Project loaded = ProjectSerializer.Deserialize(json);
        Assert.Equal(absolute, loaded.MediaPool.Get(ClipId)!.AbsolutePath);
    }

    [Fact]
    public void A_Media_Link_Sidecar_Entry_Wins_Over_An_Inlined_Path()
    {
        // A self-contained (inline) file that also has a sidecar: the sidecar is the per-user truth and wins.
        const string stale = @"C:\old\clip.mp4";
        const string current = @"D:\new\clip.mp4";
        string json = ProjectSerializer.Serialize(OneMediaProject(stale)); // inlines the stale path
        var links = new Dictionary<MediaRefId, string> { [ClipId] = current };

        Project loaded = ProjectSerializer.Deserialize(json, projectDirectory: null, links: links);
        Assert.Equal(current, loaded.MediaPool.Get(ClipId)!.AbsolutePath);
    }

    [Fact]
    public void Pre_Step28_File_With_Inlined_Paths_Still_Loads()
    {
        // A project file written before step 28 inlined the path in the media entry. It must still load (the path
        // fields simply became additive/nullable — no schema bump).
        string json = $$"""
            {
              "schemaVersion": 1,
              "media": [
                {
                  "id": "{{ClipId.Value}}",
                  "absolutePath": "C:\\legacy\\clip.mp4",
                  "relativePath": null,
                  "info": { "durationTicks": 1200000, "hasVideo": true, "frameRate": { "num": 30, "den": 1 }, "width": 1920, "height": 1080, "hasAudio": false, "sampleRate": 0, "channels": 0 }
                }
              ],
              "timeline": { "frameRate": { "num": 30, "den": 1 }, "resolution": { "width": 1920, "height": 1080 }, "sampleRate": 48000, "tracks": [] },
              "settings": { "masterGainDb": 0.0 }
            }
            """;

        Project loaded = ProjectSerializer.Deserialize(json);
        Assert.Equal(@"C:\legacy\clip.mp4", loaded.MediaPool.Get(ClipId)!.AbsolutePath);
    }

    [Fact]
    public void Reading_A_Missing_Sidecar_Returns_An_Empty_Map_Without_Throwing()
    {
        string dir = NewTempDir();
        try
        {
            string projectPath = Path.Combine(dir, "nope.sprocket.json");
            Assert.Empty(MediaLinks.Read(projectPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Offline_Media_Is_Not_Written_To_The_Sidecar()
    {
        // A source with no local path yet (offline) has nothing to record on this machine — the sidecar skips it,
        // so it stays offline and relinkable rather than persisting a bogus empty path.
        string dir = NewTempDir();
        try
        {
            var project = OneMediaProject(string.Empty); // offline
            string projectPath = Path.Combine(dir, "project.sprocket.json");
            ProjectSerializer.Save(project, projectPath);

            Assert.Empty(MediaLinks.Read(projectPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
