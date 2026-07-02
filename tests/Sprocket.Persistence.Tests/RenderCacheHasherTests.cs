using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// The render-cache content hash (PLAN.md step 32, ARCHITECTURE.md §20): a pure function of the range's
/// serializable state, so it is stable across identical models, changes for any edit that can affect the range's
/// rendered output, and does NOT change for edits outside the range or in the other scope — that independence is
/// what makes invalidation exact and undo re-validating.
/// </summary>
public class RenderCacheHasherTests
{
    private static readonly Timecode RangeIn = Timecode.Zero;
    private static readonly Timecode RangeOut = Timecode.FromSeconds(2);

    /// <summary>A two-track project: one 2 s generator video clip at 0 and one 2 s (media-less) audio clip at 0,
    /// plus a second video clip far outside the hashed range. No real media files, so the hash is file-system
    /// independent (offline sources describe as zero identity).</summary>
    private static Project BuildProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);

        var video = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF203040");
        video.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(2), Timecode.Zero));
        // A clip far outside [0, 2s): edits to it must not touch the range hash.
        video.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(2), Timecode.FromSeconds(10)));
        timeline.Tracks.Add(video);

        var audio = new AudioTrack { Name = "A1" };
        audio.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero));
        timeline.Tracks.Add(audio);

        return project;
    }

    private static string VideoHash(Project p) =>
        RenderCacheHasher.ComputeHash(p, p.ActiveSequence.Id, RangeIn, RangeOut, RenderCacheScope.Video);

    private static string AudioHash(Project p) =>
        RenderCacheHasher.ComputeHash(p, p.ActiveSequence.Id, RangeIn, RangeOut, RenderCacheScope.Audio);

    [Fact]
    public void Identical_Models_Hash_Identically()
    {
        Project a = BuildProject();
        Project b = BuildProject();
        // MediaRefIds differ between the two builds only on the audio clip — compare video scope, which has none.
        Assert.Equal(VideoHash(a), VideoHash(b));
    }

    [Fact]
    public void Hash_Is_Stable_Across_Repeated_Computation()
    {
        Project p = BuildProject();
        Assert.Equal(VideoHash(p), VideoHash(p));
        Assert.Equal(AudioHash(p), AudioHash(p));
    }

    [Fact]
    public void An_Effect_On_An_Overlapping_Clip_Changes_The_Video_Hash_And_Undo_Restores_It()
    {
        Project p = BuildProject();
        string before = VideoHash(p);

        Clip clip = p.Timeline.VideoTracks.First().Clips[0];
        var effect = new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5);
        clip.Effects.Add(effect);
        Assert.NotEqual(before, VideoHash(p));

        clip.Effects.Remove(effect); // the undo of the edit
        Assert.Equal(before, VideoHash(p));
    }

    [Fact]
    public void An_Edit_Outside_The_Range_Does_Not_Change_The_Hash()
    {
        Project p = BuildProject();
        string before = VideoHash(p);

        Clip outside = p.Timeline.VideoTracks.First().Clips[1]; // at 10 s, outside [0, 2 s)
        outside.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.9));
        outside.TimelineStart = Timecode.FromSeconds(12);

        Assert.Equal(before, VideoHash(p));
    }

    [Fact]
    public void Moving_An_Outside_Clip_Into_The_Range_Changes_The_Hash()
    {
        Project p = BuildProject();
        string before = VideoHash(p);

        p.Timeline.VideoTracks.First().Clips[1].TimelineStart = Timecode.FromSeconds(1); // now overlaps [0, 2 s)
        Assert.NotEqual(before, VideoHash(p));
    }

    [Fact]
    public void Audio_Edits_Do_Not_Invalidate_Video_And_Vice_Versa()
    {
        Project p = BuildProject();
        string video = VideoHash(p);
        string audio = AudioHash(p);

        // An audio-only edit: track gain. Video hash must hold; audio hash must change.
        p.Timeline.AudioTracks.First().GainDb = -6;
        Assert.Equal(video, VideoHash(p));
        Assert.NotEqual(audio, AudioHash(p));

        // A video-only edit: track opacity. Audio hash must hold; video hash must change.
        string audio2 = AudioHash(p);
        p.Timeline.VideoTracks.First().Opacity = 0.5;
        Assert.NotEqual(video, VideoHash(p));
        Assert.Equal(audio2, AudioHash(p));
    }

    [Fact]
    public void Master_Chain_And_Bus_Affect_Only_The_Audio_Hash()
    {
        Project p = BuildProject();
        string video = VideoHash(p);
        string audio = AudioHash(p);

        p.Settings.MasterGainDb = -3;
        p.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioGain).Set(EffectParamNames.GainDb, 2.0));

        Assert.Equal(video, VideoHash(p));
        Assert.NotEqual(audio, AudioHash(p));
    }

    [Fact]
    public void A_Nested_Child_Edit_Changes_The_Parent_Range_Hash()
    {
        Project p = BuildProject();

        // A child sequence with content, nested into the parent's hashed range.
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var childTrack = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF446688");
        Clip childClip = Clip.CreateGenerator(spec, Timecode.FromSeconds(2), Timecode.Zero);
        childTrack.Clips.Add(childClip);
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        p.Sequences.Add(child);

        p.Timeline.VideoTracks.First().Clips.Add(
            Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(1), Timecode.FromSeconds(0.5)));
        string before = VideoHash(p);

        // Edit INSIDE the child only — the parent's own tracks are untouched.
        childClip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.7));
        Assert.NotEqual(before, VideoHash(p));
    }

    [Fact]
    public void Replacing_A_Referenced_Media_File_Changes_The_Hash()
    {
        string mediaPath = Path.Combine(Path.GetTempPath(), $"sprocket-hash-media-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(mediaPath, [1, 2, 3]);
            var timeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000);
            var project = new Project(timeline);
            var id = MediaRefId.New();
            var info = new ProbedMediaInfo(Timecode.FromSeconds(2), true, new Rational(30, 1), 640, 480, false, 0, 0);
            project.MediaPool.Add(new MediaRef(id, mediaPath, info));
            var track = new VideoTrack { Name = "V1" };
            track.Clips.Add(new Clip(id, Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero));
            timeline.Tracks.Add(track);

            string before = VideoHash(project);
            File.WriteAllBytes(mediaPath, [1, 2, 3, 4]); // same path, different bytes (size + mtime change)
            Assert.NotEqual(before, VideoHash(project));
        }
        finally
        {
            try { File.Delete(mediaPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Scope_Is_Part_Of_The_Key()
    {
        Project p = BuildProject();
        Assert.NotEqual(VideoHash(p), AudioHash(p));
    }

    [Fact]
    public void Unknown_Sequence_Throws()
    {
        Project p = BuildProject();
        Assert.Throws<ArgumentException>(() =>
            RenderCacheHasher.ComputeHash(p, SequenceId.New(), RangeIn, RangeOut, RenderCacheScope.Video));
    }
}
