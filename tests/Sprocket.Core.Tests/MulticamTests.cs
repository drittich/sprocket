using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Headless tests for multicam editing &amp; clip sync (PLAN.md step 24): the multicam model, the render-graph
/// resolution of the active angle to a synced media frame, angle switching + blade behaviour, the sync-offset
/// computation (<see cref="ClipSync"/>), audio-waveform cross-correlation (<see cref="AudioSync"/>), the
/// commands, and the <see cref="MulticamBuilder"/> create-from-clips gesture. All pure model — no Skia/FFmpeg/GPU.
/// </summary>
public class MulticamModelTests
{
    [Fact]
    public void Multicam_Clip_Factory_Sets_Kind_Reference_And_Angle()
    {
        var id = MulticamId.New();
        Clip clip = Clip.CreateMulticamClip(id, activeAngle: 2, Timecode.FromSeconds(5), Timecode.FromSeconds(3));
        Assert.Equal(ClipKind.Multicam, clip.Kind);
        Assert.Equal(id, clip.SourceMulticamId);
        Assert.Equal(2, clip.ActiveAngle);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(3), clip.TimelineStart);
    }

    [Fact]
    public void AngleAt_Is_Bounds_Checked()
    {
        var source = new MulticamSource(MulticamId.New(), "Multicam 1",
        [
            new MulticamAngle("Cam 1", MediaRefId.New()),
            new MulticamAngle("Cam 2", MediaRefId.New()),
        ]);
        Assert.Equal("Cam 1", source.AngleAt(0)!.Name);
        Assert.Equal("Cam 2", source.AngleAt(1)!.Name);
        Assert.Null(source.AngleAt(2));
        Assert.Null(source.AngleAt(-1));
    }

    [Fact]
    public void Effective_Audio_Falls_Back_To_The_Video_Source()
    {
        var video = MediaRefId.New();
        var audio = MediaRefId.New();
        Assert.Equal(video, new MulticamAngle("Cam", video).EffectiveAudioRefId);             // same file
        Assert.Equal(audio, new MulticamAngle("Cam", video, audioMediaRefId: audio).EffectiveAudioRefId); // dual-system
    }

    [Fact]
    public void Get_Multicam_Finds_By_Id_And_Misses_Gracefully()
    {
        var project = new Project();
        var source = new MulticamSource(MulticamId.New(), "Multicam 1");
        project.MulticamSources.Add(source);
        Assert.Same(source, project.GetMulticam(source.Id));
        Assert.Null(project.GetMulticam(MulticamId.New()));
    }
}

public class MulticamRenderTests
{
    /// <summary>A project with a two-angle multicam source and one multicam clip on a video + an audio track.</summary>
    private static Project ProjectWithMulticam(
        out MulticamSource source, out Clip videoClip, out Clip audioClip,
        Timecode? offset0 = null, Timecode? offset1 = null, MediaRefId? audio1 = null)
    {
        var camA = MediaRefId.New();
        var camB = MediaRefId.New();
        source = new MulticamSource(MulticamId.New(), "Multicam 1",
        [
            new MulticamAngle("Cam 1", camA, offset0 ?? Timecode.Zero),
            new MulticamAngle("Cam 2", camB, offset1 ?? Timecode.Zero, audio1),
        ]);

        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.MulticamSources.Add(source);

        var v = new VideoTrack { Name = "V1" };
        videoClip = Clip.CreateMulticamClip(source.Id, 0, Timecode.FromSeconds(10), Timecode.FromSeconds(2));
        v.Clips.Add(videoClip);
        var a = new AudioTrack { Name = "A1" };
        audioClip = Clip.CreateMulticamClip(source.Id, 0, Timecode.FromSeconds(10), Timecode.FromSeconds(2));
        a.Clips.Add(audioClip);
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        return project;
    }

    [Fact]
    public void Active_Angle_Resolves_To_A_Media_Layer_At_The_Synced_Time()
    {
        // Cam 2 has a +1s sync offset. The clip starts at 2s, so at t=5s the multicam time is 3s and Cam 2's
        // source frame is at 3s + 1s = 4s.
        Project project = ProjectWithMulticam(out MulticamSource source, out Clip videoClip, out _,
            offset1: Timecode.FromSeconds(1));
        videoClip.ActiveAngle = 1;

        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(5)).Layers);
        Assert.Equal(LayerKind.Media, layer.Kind);
        Assert.Equal(source.Angles[1].MediaRefId, layer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(4), layer.SourceTime);
    }

    [Fact]
    public void Switching_The_Active_Angle_Changes_The_Resolved_Source()
    {
        Project project = ProjectWithMulticam(out MulticamSource source, out Clip videoClip, out _);

        videoClip.ActiveAngle = 0;
        Assert.Equal(source.Angles[0].MediaRefId,
            Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(5)).Layers).MediaRefId);

        videoClip.ActiveAngle = 1;
        Assert.Equal(source.Angles[1].MediaRefId,
            Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(5)).Layers).MediaRefId);
    }

    [Fact]
    public void Out_Of_Range_Angle_Contributes_No_Layer()
    {
        Project project = ProjectWithMulticam(out _, out Clip videoClip, out _);
        videoClip.ActiveAngle = 99; // beyond the two angles
        Assert.Empty(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(5)).Layers);
    }

    [Fact]
    public void Missing_Multicam_Reference_Contributes_No_Layer()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack();
        track.Clips.Add(Clip.CreateMulticamClip(MulticamId.New(), 0, Timecode.FromSeconds(5), Timecode.Zero)); // dangling
        timeline.Tracks.Add(track);
        Assert.Empty(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);
    }

    [Fact]
    public void Audio_Resolves_The_Active_Angles_Audio_Source_At_The_Synced_Time()
    {
        // Cam 2 has a separate audio source and a +1s offset; the audio layer must pull that source at the synced
        // start. Clip starts at 2s; for the buffer at t=5s the multicam time is 3s → audio source 3s + 1s = 4s.
        var dualSystemAudio = MediaRefId.New();
        Project project = ProjectWithMulticam(out MulticamSource source, out Clip videoClip, out Clip audioClip,
            offset1: Timecode.FromSeconds(1), audio1: dualSystemAudio);
        videoClip.ActiveAngle = 1;
        audioClip.ActiveAngle = 1;

        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.FromSeconds(5), Timecode.FromSeconds(0.1));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(dualSystemAudio, layer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(4), layer.SourceStart);
    }

    [Fact]
    public void Blade_Split_Keeps_Both_Halves_Multicam_With_The_Same_Angle()
    {
        Project project = ProjectWithMulticam(out MulticamSource source, out Clip videoClip, out _);
        videoClip.ActiveAngle = 1;
        var track = (VideoTrack)project.Timeline.Tracks[0];
        var history = new EditHistory();

        var split = new SplitClipCommand(track, videoClip, Timecode.FromSeconds(6));
        history.Execute(split);

        Clip right = split.RightClip;
        Assert.Equal(ClipKind.Multicam, right.Kind);
        Assert.Equal(source.Id, right.SourceMulticamId);
        Assert.Equal(1, right.ActiveAngle); // the new segment keeps the angle until it is switched
        // The two halves still sum to the original timeline span.
        Assert.Equal(videoClip.TimelineEnd, right.TimelineStart);
    }
}

public class ClipSyncTests
{
    [Fact]
    public void Offsets_Are_Relative_To_The_Reference_Angle()
    {
        IReadOnlyList<Timecode> offsets = ClipSync.ComputeOffsets(
            [Timecode.Zero, Timecode.FromSeconds(5), Timecode.FromSeconds(-3)]);
        Assert.Equal(Timecode.Zero, offsets[0]);                 // reference
        Assert.Equal(Timecode.FromSeconds(5), offsets[1]);
        Assert.Equal(Timecode.FromSeconds(-3), offsets[2]);
    }

    [Fact]
    public void Reference_Index_Rebases_The_Offsets()
    {
        IReadOnlyList<Timecode> offsets = ClipSync.ComputeOffsets(
            [Timecode.Zero, Timecode.FromSeconds(5), Timecode.FromSeconds(-3)], referenceIndex: 1);
        Assert.Equal(Timecode.FromSeconds(-5), offsets[0]);
        Assert.Equal(Timecode.Zero, offsets[1]);                 // the chosen reference
        Assert.Equal(Timecode.FromSeconds(-8), offsets[2]);
    }

    [Fact]
    public void Angle_Source_Time_Adds_The_Sync_Offset()
    {
        var angle = new MulticamAngle("Cam", MediaRefId.New(), Timecode.FromSeconds(2));
        Assert.Equal(Timecode.FromSeconds(7), ClipSync.AngleSourceTime(angle, Timecode.FromSeconds(5)));
    }

    [Fact]
    public void Apply_Offsets_Writes_Each_Angle()
    {
        var source = new MulticamSource(MulticamId.New(), "M",
            [new MulticamAngle("A", MediaRefId.New()), new MulticamAngle("B", MediaRefId.New())]);
        ClipSync.ApplyOffsets(source, [Timecode.FromSeconds(1), Timecode.FromSeconds(2)]);
        Assert.Equal(Timecode.FromSeconds(1), source.Angles[0].SyncOffset);
        Assert.Equal(Timecode.FromSeconds(2), source.Angles[1].SyncOffset);
        Assert.Throws<ArgumentException>(() => ClipSync.ApplyOffsets(source, [Timecode.Zero])); // wrong count
    }
}

public class AudioSyncTests
{
    /// <summary>A buffer of <paramref name="length"/> samples that is silent except for a triangular pulse peaking
    /// at <paramref name="center"/> — a sharp, non-periodic feature so cross-correlation has a single clear peak.</summary>
    private static float[] PulseAt(int length, int center, int halfWidth = 10)
    {
        var buffer = new float[length];
        for (int n = 0; n < length; n++)
        {
            int dist = Math.Abs(n - center);
            buffer[n] = dist < halfWidth ? halfWidth - dist : 0f;
        }
        return buffer;
    }

    [Fact]
    public void Recovers_A_Known_Delay()
    {
        // The candidate's feature sits 5 samples later than the reference's → lag 5 (candidate is delayed by 5).
        float[] reference = PulseAt(200, 80);
        float[] candidate = PulseAt(200, 85);
        AudioSyncResult result = AudioSync.FindBestLag(reference, candidate, maxLagSamples: 30);
        Assert.Equal(5, result.LagSamples);
        Assert.True(result.Confidence > 0.99, $"confidence {result.Confidence}");
    }

    [Fact]
    public void Recovers_A_Negative_Delay()
    {
        // Candidate's feature is earlier → negative lag.
        float[] reference = PulseAt(200, 100);
        float[] candidate = PulseAt(200, 93);
        Assert.Equal(-7, AudioSync.FindBestLag(reference, candidate, maxLagSamples: 30).LagSamples);
    }

    [Fact]
    public void Identical_Signals_Have_Zero_Lag_And_Full_Confidence()
    {
        float[] signal = PulseAt(200, 100);
        AudioSyncResult result = AudioSync.FindBestLag(signal, signal, maxLagSamples: 30);
        Assert.Equal(0, result.LagSamples);
        Assert.Equal(1.0, result.Confidence, 6);
    }

    [Fact]
    public void Empty_Input_Yields_Zero()
    {
        AudioSyncResult result = AudioSync.FindBestLag([], PulseAt(50, 25), maxLagSamples: 10);
        Assert.Equal(0, result.LagSamples);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void Best_Offset_Converts_The_Lag_To_Ticks()
    {
        float[] reference = PulseAt(200, 80);
        float[] candidate = PulseAt(200, 85);
        (Timecode offset, double confidence) = AudioSync.FindBestOffset(reference, candidate, sampleRate: 48000, maxLagSamples: 30);
        Assert.Equal(Timecode.FromSamples(5, 48000), offset); // 5 samples → 25 ticks at 48 kHz
        Assert.True(confidence > 0.99);
    }

    [Fact]
    public void Negative_Max_Lag_Is_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSync.FindBestLag(PulseAt(50, 25), PulseAt(50, 25), -1));
    }
}

public class MulticamCommandTests
{
    [Fact]
    public void Set_Angle_Command_Applies_And_Reverts()
    {
        var clip = Clip.CreateMulticamClip(MulticamId.New(), 0, Timecode.FromSeconds(5), Timecode.Zero);
        var history = new EditHistory();

        history.Execute(new SetClipAngleCommand(clip, 2));
        Assert.Equal(2, clip.ActiveAngle);

        history.Undo();
        Assert.Equal(0, clip.ActiveAngle);

        history.Redo();
        Assert.Equal(2, clip.ActiveAngle);
    }

    [Fact]
    public void Add_Multicam_Source_Command_Adds_And_Reverts()
    {
        var project = new Project();
        var history = new EditHistory();
        var source = new MulticamSource(MulticamId.New(), "Multicam 1");

        history.Execute(new AddMulticamSourceCommand(project, source));
        Assert.Contains(source, project.MulticamSources);

        history.Undo();
        Assert.DoesNotContain(source, project.MulticamSources);

        history.Redo();
        Assert.Contains(source, project.MulticamSources);
    }

    [Fact]
    public void Remove_Multicam_Source_Restores_At_Index_On_Undo()
    {
        var project = new Project();
        var history = new EditHistory();
        var first = new MulticamSource(MulticamId.New(), "First");
        var second = new MulticamSource(MulticamId.New(), "Second");
        project.MulticamSources.Add(first);
        project.MulticamSources.Add(second);

        history.Execute(new RemoveMulticamSourceCommand(project, first));
        Assert.DoesNotContain(first, project.MulticamSources);

        history.Undo();
        Assert.Equal(0, project.MulticamSources.IndexOf(first));
    }

    [Fact]
    public void Set_Offsets_Command_Applies_And_Reverts_All_Angles()
    {
        var source = new MulticamSource(MulticamId.New(), "M",
            [new MulticamAngle("A", MediaRefId.New()), new MulticamAngle("B", MediaRefId.New(), Timecode.FromSeconds(1))]);
        var history = new EditHistory();

        history.Execute(new SetMulticamOffsetsCommand(source, [Timecode.FromSeconds(3), Timecode.FromSeconds(4)]));
        Assert.Equal(Timecode.FromSeconds(3), source.Angles[0].SyncOffset);
        Assert.Equal(Timecode.FromSeconds(4), source.Angles[1].SyncOffset);

        history.Undo();
        Assert.Equal(Timecode.Zero, source.Angles[0].SyncOffset);
        Assert.Equal(Timecode.FromSeconds(1), source.Angles[1].SyncOffset);
    }
}

public class MulticamBuilderTests
{
    /// <summary>A project with three stacked video angle clips (one per track), each linked to a companion audio
    /// clip on its own audio track, all placed at the origin.</summary>
    private static Project ThreeAngleProject(out List<Clip> videoClips)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        videoClips = new List<Clip>();
        for (int i = 0; i < 3; i++)
        {
            var media = MediaRefId.New();
            var link = Guid.NewGuid();
            var v = new VideoTrack { Name = $"V{i + 1}" };
            var vc = new Clip(media, Timecode.Zero, Timecode.FromSeconds(8), Timecode.Zero) { LinkGroupId = link };
            v.Clips.Add(vc);
            var a = new AudioTrack { Name = $"A{i + 1}" };
            a.Clips.Add(new Clip(media, Timecode.Zero, Timecode.FromSeconds(8), Timecode.Zero) { LinkGroupId = link });
            timeline.Tracks.Add(v);
            timeline.Tracks.Add(a);
            videoClips.Add(vc);
        }
        return project;
    }

    [Fact]
    public void Create_Builds_A_Source_And_Replaces_The_Selection_With_A_Linked_Pair()
    {
        Project project = ThreeAngleProject(out List<Clip> videoClips);
        var history = new EditHistory();

        MulticamBuilder.MulticamResult? result = MulticamBuilder.CreateMulticam(
            project, project.ActiveSequence, videoClips, "Multicam 1");
        Assert.NotNull(result);
        history.Execute(result!.Command);

        // The source has one angle per selected video clip.
        MulticamSource source = result.Source;
        Assert.Contains(source, project.MulticamSources);
        Assert.Equal(3, source.Angles.Count);
        Assert.Equal(videoClips[0].MediaRefId, source.Angles[0].MediaRefId);

        // The primary clip is a multicam clip on the first video track, referencing the source.
        Clip primary = result.PrimaryClip;
        Assert.Equal(ClipKind.Multicam, primary.Kind);
        Assert.Equal(source.Id, primary.SourceMulticamId);
        Assert.Equal(0, primary.ActiveAngle);

        // The original angle clips (and their audio companions) are gone, replaced by a linked V/A multicam pair.
        var multicamClips = project.Timeline.Tracks
            .SelectMany(t => t.Clips).Where(c => c.Kind == ClipKind.Multicam).ToList();
        Assert.Equal(2, multicamClips.Count); // one on a video track, one on an audio track
        Assert.NotNull(multicamClips[0].LinkGroupId);
        Assert.Equal(multicamClips[0].LinkGroupId, multicamClips[1].LinkGroupId);
        Assert.DoesNotContain(project.Timeline.Tracks.SelectMany(t => t.Clips), c => c.Kind == ClipKind.Media);
    }

    [Fact]
    public void Create_Is_One_Undoable_Step()
    {
        Project project = ThreeAngleProject(out List<Clip> videoClips);
        var history = new EditHistory();
        MulticamBuilder.MulticamResult result = MulticamBuilder.CreateMulticam(
            project, project.ActiveSequence, videoClips, "Multicam 1")!;
        history.Execute(result.Command);

        history.Undo();
        // The source is gone and every original clip is back.
        Assert.Empty(project.MulticamSources);
        Assert.DoesNotContain(project.Timeline.Tracks.SelectMany(t => t.Clips), c => c.Kind == ClipKind.Multicam);
        Assert.Same(videoClips[0], project.Timeline.Tracks[0].Clips.Single());
    }

    [Fact]
    public void Create_Renders_The_Active_Angle_Through_The_New_Clip()
    {
        Project project = ThreeAngleProject(out List<Clip> videoClips);
        MediaRefId cam2 = videoClips[1].MediaRefId;
        var history = new EditHistory();
        MulticamBuilder.MulticamResult result = MulticamBuilder.CreateMulticam(
            project, project.ActiveSequence, videoClips, "Multicam 1")!;
        history.Execute(result.Command);

        // Switch the multicam clip to Cam 2; planning resolves Cam 2's source (angles aligned at the origin).
        result.PrimaryClip.ActiveAngle = 1;
        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(3)).Layers);
        Assert.Equal(cam2, layer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(3), layer.SourceTime); // origin-aligned, zero offset
    }

    [Fact]
    public void Fewer_Than_Two_Angles_Is_Not_A_Multicam()
    {
        Project project = ThreeAngleProject(out List<Clip> videoClips);
        Assert.Null(MulticamBuilder.CreateMulticam(project, project.ActiveSequence, [videoClips[0]], "Multicam 1"));
    }
}
