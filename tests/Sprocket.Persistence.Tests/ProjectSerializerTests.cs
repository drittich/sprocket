using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Round-trip and resilience tests for <see cref="ProjectSerializer"/> (PLAN.md step 9, ARCHITECTURE.md §12).
/// All headless — the model is pure data, so these never touch FFmpeg, Skia, or a real media file (except the
/// relative-path test, which only needs an empty file on disk to resolve against).
/// </summary>
public class ProjectSerializerTests
{
    private static readonly MediaRefId VideoId = MediaRefId.New();
    private static readonly MediaRefId AudioId = MediaRefId.New();
    private static readonly Guid LinkGroup = Guid.NewGuid();

    /// <summary>A project exercising every field the format must preserve: two track kinds, trim, track
    /// gain/mute/solo and opacity/blend, a constant and a keyframed effect, and a non-default master gain.</summary>
    private static Project BuildRichProject()
    {
        var timeline = new Timeline(new Rational(30000, 1001), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.Settings.MasterGainDb = -2.5;

        project.MediaPool.Add(new MediaRef(VideoId, @"C:\media\clip.mp4",
            new ProbedMediaInfo(Timecode.FromSeconds(12.5), true, new Rational(30000, 1001), 1920, 1080, true, 48000, 2)));
        project.MediaPool.Add(new MediaRef(AudioId, @"C:\media\music.wav",
            new ProbedMediaInfo(Timecode.FromSeconds(60), false, Rational.Zero, 0, 0, true, 44100, 2)));

        var video = new VideoTrack { Name = "V1", Enabled = true, Opacity = 0.8, BlendMode = BlendMode.Multiply };
        var clip = new Clip(VideoId, Timecode.FromSeconds(1), Timecode.FromSeconds(6), Timecode.FromSeconds(2)) { LinkGroupId = LinkGroup };
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity,
            AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 0.0, Interpolation.Linear),
                new Keyframe(Timecode.FromSeconds(1), 1.0, Interpolation.Hold),
            ])));
        video.Clips.Add(clip);

        var audio = new AudioTrack { Name = "A1", Enabled = true, GainDb = -3.0, Muted = true, Solo = false };
        audio.Clips.Add(new Clip(AudioId, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero) { LinkGroupId = LinkGroup });

        // Track order is z-order and must be preserved exactly.
        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        return project;
    }

    private static Project RoundTrip(Project project)
        => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    [Fact]
    public void Round_Trips_Timeline_Format_And_Settings()
    {
        Project loaded = RoundTrip(BuildRichProject());

        Assert.Equal(new Rational(30000, 1001), loaded.Timeline.FrameRate);
        Assert.Equal(new Resolution(1920, 1080), loaded.Timeline.Resolution);
        Assert.Equal(48000, loaded.Timeline.SampleRate);
        Assert.Equal(-2.5, loaded.Settings.MasterGainDb);
    }

    [Fact]
    public void Round_Trips_Media_Pool()
    {
        Project loaded = RoundTrip(BuildRichProject());

        MediaRef? video = loaded.MediaPool.Get(VideoId);
        Assert.NotNull(video);
        Assert.Equal(@"C:\media\clip.mp4", video.AbsolutePath);
        Assert.True(video.Info.HasVideo);
        Assert.Equal(Timecode.FromSeconds(12.5).Ticks, video.Info.Duration.Ticks);
        Assert.Equal(new Rational(30000, 1001), video.Info.FrameRate);

        MediaRef? audio = loaded.MediaPool.Get(AudioId);
        Assert.NotNull(audio);
        Assert.False(audio.Info.HasVideo);
        Assert.Equal(44100, audio.Info.SampleRate);
    }

    [Fact]
    public void Round_Trips_Tracks_In_Z_Order_With_Their_Properties()
    {
        Project loaded = RoundTrip(BuildRichProject());

        Assert.Collection(loaded.Timeline.Tracks,
            t =>
            {
                var v = Assert.IsType<VideoTrack>(t);
                Assert.Equal("V1", v.Name);
                Assert.Equal(0.8, v.Opacity);
                Assert.Equal(BlendMode.Multiply, v.BlendMode);
            },
            t =>
            {
                var a = Assert.IsType<AudioTrack>(t);
                Assert.Equal("A1", a.Name);
                Assert.Equal(-3.0, a.GainDb);
                Assert.True(a.Muted);
                Assert.False(a.Solo);
            });
    }

    [Fact]
    public void Round_Trips_Clip_Trim_And_Placement()
    {
        Clip clip = RoundTrip(BuildRichProject()).Timeline.VideoTracks.First().Clips.Single();

        Assert.Equal(VideoId, clip.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(1).Ticks, clip.SourceIn.Ticks);
        Assert.Equal(Timecode.FromSeconds(6).Ticks, clip.SourceOut.Ticks);
        Assert.Equal(Timecode.FromSeconds(2).Ticks, clip.TimelineStart.Ticks);
    }

    [Fact]
    public void Round_Trips_The_Clip_Link_Relation()
    {
        Timeline loaded = RoundTrip(BuildRichProject()).Timeline;
        Clip video = loaded.VideoTracks.First().Clips.Single();
        Clip audio = loaded.AudioTracks.First().Clips.Single();

        Assert.Equal(LinkGroup, video.LinkGroupId);
        Assert.Equal(LinkGroup, audio.LinkGroupId);
        // The relation survives: the loaded clips are each other's companion.
        Assert.Same(audio, Assert.Single(loaded.ClipsLinkedTo(video)).Clip);
    }

    [Fact]
    public void Round_Trips_Constant_And_Keyframed_Effects()
    {
        Clip clip = RoundTrip(BuildRichProject()).Timeline.VideoTracks.First().Clips.Single();
        Assert.Equal(2, clip.Effects.Count);

        EffectInstance brightness = clip.Effects[0];
        Assert.Equal(EffectTypeIds.Brightness, brightness.EffectTypeId);
        AnimatableValue amount = brightness.Parameters[EffectParamNames.Amount];
        Assert.False(amount.IsAnimated);
        Assert.Equal(1.2, amount.Evaluate(Timecode.Zero));

        EffectInstance fade = clip.Effects[1];
        Assert.Equal(EffectTypeIds.Fade, fade.EffectTypeId);
        AnimatableValue opacity = fade.Parameters[EffectParamNames.Opacity];
        Assert.True(opacity.IsAnimated);
        Assert.Collection(opacity.Keyframes,
            k => { Assert.Equal(0, k.Time.Ticks); Assert.Equal(0.0, k.Value); Assert.Equal(Interpolation.Linear, k.Interpolation); },
            k => { Assert.Equal(Timecode.FromSeconds(1).Ticks, k.Time.Ticks); Assert.Equal(1.0, k.Value); Assert.Equal(Interpolation.Hold, k.Interpolation); });
    }

    [Fact]
    public void Round_Trips_Eased_Interpolation_Modes()
    {
        // The step-16d eased modes serialize via the string enum converter (additive — no schema bump).
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Transform).Set(EffectParamNames.Scale,
            AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 1.0, Interpolation.EaseOut),
                new Keyframe(Timecode.FromSeconds(2), 1.5, Interpolation.EaseIn),
                new Keyframe(Timecode.FromSeconds(4), 1.0, Interpolation.EaseInOut),
            ])));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        AnimatableValue scale = RoundTrip(project).Timeline.VideoTracks.First()
            .Clips.Single().Effects.Single().Parameters[EffectParamNames.Scale];
        Assert.Collection(scale.Keyframes,
            k => Assert.Equal(Interpolation.EaseOut, k.Interpolation),
            k => Assert.Equal(Interpolation.EaseIn, k.Interpolation),
            k => Assert.Equal(Interpolation.EaseInOut, k.Interpolation));
        // The eased value survives the trip too (midpoint of an EaseOut segment is below linear).
        Assert.Equal(1.125, scale.Evaluate(Timecode.FromSeconds(1)), 6); // 1.0 + 0.5*(0.5²)=1.125
    }

    [Fact]
    public void Round_Trips_Custom_Bezier_Handles()
    {
        // The step-16d custom velocity handles serialize as nullable additive fields (no schema bump).
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Transform).Set(EffectParamNames.Scale,
            AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 1.0, Interpolation.Bezier, EaseOut: new BezierHandle(0.2, 0.1)),
                new Keyframe(Timecode.FromSeconds(4), 2.0, EaseIn: new BezierHandle(0.8, 0.9)),
            ])));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        AnimatableValue scale = RoundTrip(project).Timeline.VideoTracks.First()
            .Clips.Single().Effects.Single().Parameters[EffectParamNames.Scale];
        Assert.Equal(Interpolation.Bezier, scale.Keyframes[0].Interpolation);
        Assert.Equal(new BezierHandle(0.2, 0.1), scale.Keyframes[0].EaseOut);
        Assert.Equal(new BezierHandle(0.8, 0.9), scale.Keyframes[1].EaseIn);
        Assert.Null(scale.Keyframes[1].EaseOut); // unset handles stay null (and serialize as omitted)
    }

    [Fact]
    public void Round_Trips_Generator_And_Adjustment_Clips()
    {
        // PLAN.md step 19: a generator clip (with string + keyframed-numeric params) and an adjustment layer.
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);

        var genTrack = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.Title)
            .SetString(GeneratorParamNames.Text, "Hello")
            .SetString(GeneratorParamNames.Color, "#FFEEDDCC")
            .Set(GeneratorParamNames.FontSize, AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 0.1),
                new Keyframe(Timecode.FromSeconds(2), 0.2),
            ]));
        Clip generator = Clip.CreateGenerator(spec, Timecode.FromSeconds(3), Timecode.FromSeconds(1));
        generator.SourceIn = Timecode.FromSeconds(0.5); // a trimmed generator: non-zero source in must survive
        genTrack.Clips.Add(generator);

        var adjTrack = new VideoTrack { Name = "V2" };
        Clip adjustment = Clip.CreateAdjustment(Timecode.FromSeconds(4), Timecode.Zero);
        adjustment.Effects.Add(new EffectInstance(EffectTypeIds.Color).Set(EffectParamNames.Saturation, 0.0));
        adjTrack.Clips.Add(adjustment);

        timeline.Tracks.Add(genTrack);
        timeline.Tracks.Add(adjTrack);

        Timeline loaded = RoundTrip(project).Timeline;
        Clip g = loaded.VideoTracks.First().Clips.Single();
        Assert.Equal(ClipKind.Generator, g.Kind);
        Assert.NotNull(g.Generator);
        Assert.Equal(GeneratorTypeIds.Title, g.Generator!.GeneratorTypeId);
        Assert.Equal("Hello", g.Generator.GetString(GeneratorParamNames.Text));
        Assert.Equal("#FFEEDDCC", g.Generator.GetString(GeneratorParamNames.Color));
        Assert.Equal(0.15, g.Generator.Parameters[GeneratorParamNames.FontSize].Evaluate(Timecode.FromSeconds(1)), 6);
        Assert.Equal(Timecode.FromSeconds(0.5).Ticks, g.SourceIn.Ticks);
        Assert.Equal(Timecode.FromSeconds(3).Ticks, g.SourceOut.Ticks);
        Assert.Equal(Timecode.FromSeconds(1).Ticks, g.TimelineStart.Ticks);

        Clip a = loaded.VideoTracks.Last().Clips.Single();
        Assert.Equal(ClipKind.Adjustment, a.Kind);
        Assert.Null(a.Generator);
        Assert.Equal(EffectTypeIds.Color, Assert.Single(a.Effects).EffectTypeId);
    }

    [Fact]
    public void Media_Only_Clip_Omits_Kind_And_Generator_In_Json()
    {
        // The step-19 clip fields are additive + nullable: a plain media project must not emit them, so pre-19
        // files stay byte-identical and there is no schema bump. (The two "kind" tokens present are the per-track
        // TrackKind discriminators; the rich project has two tracks and no generator clips.)
        string json = ProjectSerializer.Serialize(BuildRichProject());
        Assert.DoesNotContain("\"generator\"", json);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(json, "\"kind\"").Count);
    }

    [Fact]
    public void Serialized_Json_Carries_The_Schema_Version()
    {
        string json = ProjectSerializer.Serialize(BuildRichProject());
        Assert.Contains($"\"schemaVersion\": {ProjectSerializer.SchemaVersion}", json);
    }

    [Fact]
    public void Empty_Project_Round_Trips()
    {
        Project loaded = RoundTrip(new Project());
        Assert.Empty(loaded.Timeline.Tracks);
        Assert.Empty(loaded.MediaPool.Items);
        Assert.Equal(0, loaded.Settings.MasterGainDb);
    }

    [Fact]
    public void Proxy_Settings_Round_Trip()
    {
        var project = new Project();
        project.Settings.UseProxies = false;
        project.Settings.ProxyTier = ProxyTier.Quarter;

        Project loaded = RoundTrip(project);
        Assert.False(loaded.Settings.UseProxies);
        Assert.Equal(ProxyTier.Quarter, loaded.Settings.ProxyTier);
    }

    [Fact]
    public void Pre_Step18_File_Without_Proxy_Settings_Loads_With_Defaults()
    {
        // A settings block from before step 18 carries only masterGainDb; the additive proxy fields must default
        // to on / Half rather than failing the load.
        const string json = """
            {
              "schemaVersion": 1,
              "media": [],
              "timeline": { "frameRate": { "num": 30, "den": 1 }, "resolution": { "width": 1920, "height": 1080 }, "sampleRate": 48000, "tracks": [] },
              "settings": { "masterGainDb": -3.0 }
            }
            """;

        Project loaded = ProjectSerializer.Deserialize(json);
        Assert.Equal(-3.0, loaded.Settings.MasterGainDb);
        Assert.True(loaded.Settings.UseProxies);
        Assert.Equal(ProxyTier.Half, loaded.Settings.ProxyTier);
    }

    [Fact]
    public void Unknown_Schema_Version_Throws()
    {
        string json = ProjectSerializer.Serialize(BuildRichProject())
            .Replace($"\"schemaVersion\": {ProjectSerializer.SchemaVersion}", "\"schemaVersion\": 999");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_Json_Throws_InvalidData()
    {
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("{ this is not json"));
    }

    [Fact]
    public void Save_And_Load_Resolve_Relative_Media_When_The_Project_Is_Moved()
    {
        // dirA: the original project + media; dirB: a "moved" copy with the media alongside. The relative
        // path stored on save must relink to dirB's media on load — not the (now-stale) dirA absolute path.
        string root = Path.Combine(Path.GetTempPath(), "sprocket-persist-" + Guid.NewGuid().ToString("N"));
        string dirA = Path.Combine(root, "a");
        string dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(Path.Combine(dirA, "media"));
        Directory.CreateDirectory(Path.Combine(dirB, "media"));
        try
        {
            string mediaA = Path.Combine(dirA, "media", "clip.mp4");
            File.WriteAllText(mediaA, "x"); // just needs to exist to be resolvable

            var timeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000);
            var project = new Project(timeline);
            var id = MediaRefId.New();
            project.MediaPool.Add(new MediaRef(id, mediaA,
                new ProbedMediaInfo(Timecode.FromSeconds(1), true, new Rational(30, 1), 640, 480, false, 0, 0)));

            string projA = Path.Combine(dirA, "project.sprocket.json");
            ProjectSerializer.Save(project, projA);

            // Move: copy the project file and the media into dirB, then load from there.
            string projB = Path.Combine(dirB, "project.sprocket.json");
            File.Copy(projA, projB);
            File.WriteAllText(Path.Combine(dirB, "media", "clip.mp4"), "x");

            Project loaded = ProjectSerializer.Load(projB);
            string resolved = loaded.MediaPool.Get(id)!.AbsolutePath;
            Assert.Equal(Path.GetFullPath(Path.Combine(dirB, "media", "clip.mp4")), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_Media_Does_Not_Fail_The_Load()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        const string offline = @"C:\does\not\exist\gone.mp4";
        project.MediaPool.Add(new MediaRef(id, offline,
            new ProbedMediaInfo(Timecode.FromSeconds(1), true, new Rational(30, 1), 640, 480, false, 0, 0)));

        // No project directory → no relative path; the absolute path is kept verbatim, offline and all (§12).
        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(offline, loaded.MediaPool.Get(id)!.AbsolutePath);
    }
}
