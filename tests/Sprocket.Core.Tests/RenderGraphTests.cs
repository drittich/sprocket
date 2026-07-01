using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class RenderGraphPlanTests
{
    private static Project ProjectWithVideoClip(out VideoTrack track, out Clip clip, out MediaRefId media)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        media = MediaRefId.New();
        track = new VideoTrack();
        clip = new Clip(media, Timecode.FromSeconds(2), Timecode.FromSeconds(6), Timecode.FromSeconds(10));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Empty_Where_No_Clip_Active()
    {
        Project project = ProjectWithVideoClip(out _, out _, out _);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(0));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Maps_Timeline_Time_To_Source_Time()
    {
        Project project = ProjectWithVideoClip(out _, out _, out MediaRefId media);
        // 1s into the clip (clip starts at 10s, source in-point 2s) -> source 3s.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(11));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(media, layer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(3), layer.SourceTime);
    }

    [Fact]
    public void Skips_Disabled_Track()
    {
        Project project = ProjectWithVideoClip(out VideoTrack track, out _, out _);
        track.Enabled = false;
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(11));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Layers_Are_Bottom_To_Top_In_Track_Order()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var bottomMedia = MediaRefId.New();
        var topMedia = MediaRefId.New();

        var bottom = new VideoTrack { Opacity = 1.0 };
        bottom.Clips.Add(new Clip(bottomMedia, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        var top = new VideoTrack { Opacity = 0.5, BlendMode = BlendMode.Screen };
        top.Clips.Add(new Clip(topMedia, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));

        project.Timeline.Tracks.Add(bottom); // index 0 = bottom
        project.Timeline.Tracks.Add(top);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        Assert.Equal(2, plan.Layers.Count);
        Assert.Equal(bottomMedia, plan.Layers[0].MediaRefId);
        Assert.Equal(topMedia, plan.Layers[1].MediaRefId);
        Assert.Equal(0.5, plan.Layers[1].Opacity);
        Assert.Equal(BlendMode.Screen, plan.Layers[1].BlendMode);
    }

    [Fact]
    public void Effects_Preserve_Stack_Order_And_Evaluate_At_T()
    {
        Project project = ProjectWithVideoClip(out _, out Clip clip, out _);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(
            EffectParamNames.Opacity,
            AnimatableValue.Animated(new[]
            {
                new Keyframe(Timecode.FromSeconds(10), 1.0),
                new Keyframe(Timecode.FromSeconds(14), 0.0),
            })));

        // 12s = clip start 10s + 2s, halfway through the 4s fade -> opacity 0.5.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(12));
        VideoLayer layer = Assert.Single(plan.Layers);

        Assert.Equal(2, layer.Effects.Count);
        Assert.Equal(EffectTypeIds.Brightness, layer.Effects[0].EffectTypeId);
        Assert.Equal(1.2, layer.Effects[0].Get(EffectParamNames.Amount), 6);
        Assert.Equal(EffectTypeIds.Fade, layer.Effects[1].EffectTypeId);
        Assert.Equal(0.5, layer.Effects[1].Get(EffectParamNames.Opacity), 6);
    }

    [Fact]
    public void Generator_Clip_Plans_A_Generator_Layer_With_Resolved_Params()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var track = new VideoTrack();
        var spec = new GeneratorSpec(GeneratorTypeIds.Title)
            .SetString(GeneratorParamNames.Text, "Hello")
            .Set(GeneratorParamNames.FontSize, AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 0.1),
                new Keyframe(Timecode.FromSeconds(2), 0.2),
            ]));
        track.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(2), Timecode.Zero));
        project.Timeline.Tracks.Add(track);

        // 1s in: font size keyframes 0.1→0.2 over 2s, so 0.15 at the midpoint.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(LayerKind.Generator, layer.Kind);
        Assert.NotNull(layer.Generator);
        Assert.Equal(GeneratorTypeIds.Title, layer.Generator!.GeneratorTypeId);
        Assert.Equal("Hello", layer.Generator.GetString(GeneratorParamNames.Text));
        Assert.Equal(0.15, layer.Generator.Get(GeneratorParamNames.FontSize), 6);
    }

    [Fact]
    public void Adjustment_Clip_Plans_An_Adjustment_Layer_Carrying_Its_Effects()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var track = new VideoTrack { Opacity = 0.8 };
        Clip adjust = Clip.CreateAdjustment(Timecode.FromSeconds(5), Timecode.Zero);
        adjust.Effects.Add(new EffectInstance(EffectTypeIds.Color).Set(EffectParamNames.Saturation, 0.0));
        track.Clips.Add(adjust);
        project.Timeline.Tracks.Add(track);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(LayerKind.Adjustment, layer.Kind);
        Assert.Null(layer.Generator);
        Assert.Equal(0.8, layer.Opacity, 6);
        Assert.Equal(EffectTypeIds.Color, Assert.Single(layer.Effects).EffectTypeId);
    }
}

public class RenderGraphExecutorTests
{
    /// <summary>
    /// A fake compositor whose "image" is a string describing the operations applied to it. Proves the
    /// executor drives the seam in the right order without any GPU/Skia dependency.
    /// </summary>
    private sealed class StringCompositor : IVideoCompositor<string>
    {
        public List<string> CompositeLog { get; } = new();

        public string CreateTransparentSurface(Resolution size) => "surface[";

        public string CreateGeneratorFrame(ResolvedGenerator generator, Resolution size, Timecode localTime) =>
            $"gen({generator.GeneratorTypeId})";

        public string ApplyEffect(string frame, ResolvedEffect effect) =>
            $"{frame}+{effect.EffectTypeId}";

        public string ApplyTransition(string from, string to, ResolvedTransition transition) =>
            $"[{from}>{transition.TransitionTypeId}@{transition.Progress:0.##}>{to}]";

        public void Composite(string surface, string layer, double opacity, BlendMode blendMode) =>
            CompositeLog.Add($"{layer}@{opacity:0.##}/{blendMode}");

        public string Snapshot(string surface) => surface + "]";
    }

    private sealed class NamedFrameSource : IFrameSource<string>
    {
        public string GetFrame(MediaRefId media, Timecode sourceTime) => $"frame({sourceTime.Ticks})";
    }

    [Fact]
    public void Render_Applies_Effects_In_Order_Then_Composites_Each_Layer()
    {
        var brightness = new ResolvedEffect(EffectTypeIds.Brightness, new Dictionary<string, double>());
        var fade = new ResolvedEffect(EffectTypeIds.Fade, new Dictionary<string, double>());
        var layer = new VideoLayer(MediaRefId.New(), new Timecode(900), new[] { brightness, fade }, 0.5, BlendMode.Multiply);
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { layer });

        var compositor = new StringCompositor();
        string result = RenderGraph.Render(plan, new NamedFrameSource(), compositor);

        // The composited layer string shows the frame fetched at the source time, then effects folded in order.
        string logged = Assert.Single(compositor.CompositeLog);
        Assert.Equal("frame(900)+builtin.brightness+builtin.fade@0.5/Multiply", logged);
        Assert.Equal("surface[]", result);
    }

    [Fact]
    public void Render_Of_Empty_Plan_Just_Returns_Snapshot()
    {
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, Array.Empty<VideoLayer>());
        var compositor = new StringCompositor();
        string result = RenderGraph.Render(plan, new NamedFrameSource(), compositor);
        Assert.Empty(compositor.CompositeLog);
        Assert.Equal("surface[]", result);
    }

    [Fact]
    public void Render_Generator_Layer_Draws_Generator_Not_Media()
    {
        var gen = new ResolvedGenerator(GeneratorTypeIds.Title,
            new Dictionary<string, string>(), new Dictionary<string, double>());
        var layer = new VideoLayer(default, Timecode.Zero, [], 1.0, BlendMode.Normal, LayerKind.Generator, gen);
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { layer });

        var compositor = new StringCompositor();
        RenderGraph.Render(plan, new NamedFrameSource(), compositor);

        // The composited content comes from the generator factory, never from the (media) frame source.
        Assert.Equal($"gen({GeneratorTypeIds.Title})@1/Normal", Assert.Single(compositor.CompositeLog));
    }

    [Fact]
    public void Render_Transition_Layer_Blends_Both_Sides_Then_Composites()
    {
        var from = new VideoLayer(MediaRefId.New(), new Timecode(100), [], 1.0, BlendMode.Normal);
        var brightness = new ResolvedEffect(EffectTypeIds.Brightness, new Dictionary<string, double>());
        var to = new VideoLayer(MediaRefId.New(), new Timecode(200), new[] { brightness }, 1.0, BlendMode.Normal);
        var tr = new ResolvedTransition(TransitionTypeIds.CrossDissolve, 0.5, new Dictionary<string, double>(), from, to);
        var layer = new VideoLayer(default, default, [], 0.7, BlendMode.Screen, LayerKind.Transition, Transition: tr);
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { layer });

        var compositor = new StringCompositor();
        RenderGraph.Render(plan, new NamedFrameSource(), compositor);

        // One composite: the two sides' (effect-folded) frames blended by the transition, then composited at the
        // track's opacity/blend. The 'to' side carries its own brightness effect; the 'from' side has none.
        string logged = Assert.Single(compositor.CompositeLog);
        Assert.Equal("[frame(100)>builtin.crossdissolve@0.5>frame(200)+builtin.brightness]@0.7/Screen", logged);
    }

    [Fact]
    public void Render_Adjustment_Layer_Applies_Effects_To_The_Composite_Beneath()
    {
        var media = new VideoLayer(MediaRefId.New(), new Timecode(100), [], 1.0, BlendMode.Normal);
        var brightness = new ResolvedEffect(EffectTypeIds.Brightness, new Dictionary<string, double>());
        var adjust = new VideoLayer(default, Timecode.Zero, new[] { brightness }, 1.0, BlendMode.Normal, LayerKind.Adjustment);
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { media, adjust });

        var compositor = new StringCompositor();
        RenderGraph.Render(plan, new NamedFrameSource(), compositor);

        // Two composites: the media frame, then the adjustment — which takes a snapshot of the surface so far
        // ("surface[]") and folds its effect over it, rather than fetching any source frame.
        Assert.Equal(2, compositor.CompositeLog.Count);
        Assert.Equal("frame(100)@1/Normal", compositor.CompositeLog[0]);
        Assert.Equal("surface[]+builtin.brightness@1/Normal", compositor.CompositeLog[1]);
    }
}

public class AudioPlanTests
{
    private static Project ProjectWithAudio(double gainDb, out AudioTrack track, out Clip clip)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track = new AudioTrack { GainDb = gainDb };
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Unity_Gain_Is_One()
    {
        Project project = ProjectWithAudio(0, out _, out _);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(1.0, layer.GainStartLinear, 6);
        Assert.Equal(1.0, layer.GainEndLinear, 6);
        Assert.Equal(1.0, plan.MasterGainLinear, 6);
    }

    [Fact]
    public void Minus_Six_Db_Is_About_Half_Amplitude()
    {
        Project project = ProjectWithAudio(-6.0206, out _, out _);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.5, Assert.Single(plan.Layers).GainStartLinear, 3);
    }

    [Fact]
    public void Muted_Track_Is_Excluded()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track, out _);
        track.Muted = true;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Solo_Excludes_Non_Soloed_Tracks()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track1, out _);
        var track2 = new AudioTrack();
        track2.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        project.Timeline.Tracks.Add(track2);

        track2.Solo = true;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(track2.Clips[0].MediaRefId, layer.MediaRefId);
    }

    [Fact]
    public void Fade_Produces_A_Gain_Ramp_Across_The_Buffer()
    {
        Project project = ProjectWithAudio(0, out _, out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(
            EffectParamNames.Opacity,
            AnimatableValue.Animated(new[]
            {
                new Keyframe(Timecode.FromSeconds(0), 0.0),
                new Keyframe(Timecode.FromSeconds(1), 1.0),
            })));

        // Buffer [0, 0.5s): fade-in ramps 0.0 -> 0.5 across the buffer.
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.5));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(0.0, layer.GainStartLinear, 6);
        Assert.Equal(0.5, layer.GainEndLinear, 6);
    }

    [Fact]
    public void Master_Gain_Is_Carried_Through()
    {
        Project project = ProjectWithAudio(0, out _, out _);
        project.Settings.MasterGainDb = -6.0206;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.5, plan.MasterGainLinear, 3);
    }

    // ---- clip gain + measurement scope (PLAN.md step 30 loudness normalization) ---------------------------

    [Fact]
    public void Clip_Gain_Folds_Into_Layer_Gain()
    {
        Project project = ProjectWithAudio(0, out _, out Clip clip);
        clip.GainDb = -6.0206;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.5, Assert.Single(plan.Layers).GainStartLinear, 3);
    }

    [Fact]
    public void Clip_And_Track_Gain_Multiply()
    {
        Project project = ProjectWithAudio(-6.0206, out _, out Clip clip);
        clip.GainDb = -6.0206;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.25, Assert.Single(plan.Layers).GainStartLinear, 3); // 0.5 × 0.5
    }

    [Fact]
    public void Scope_Only_Track_Isolates_One_Track_Ignoring_Solo()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track1, out _);
        var track2 = new AudioTrack();
        track2.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        project.Timeline.Tracks.Add(track2);
        track1.Solo = true; // even with another track soloed, the scope measures the requested track

        var scope = new AudioPlanScope(OnlyTrack: track2);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, project.ActiveSequence, Timecode.Zero, Timecode.FromSeconds(0.1), scope);
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(track2.Clips[0].MediaRefId, layer.MediaRefId);
    }

    [Fact]
    public void Scope_Unity_Track_And_Master_Gain_Measure_Raw()
    {
        Project project = ProjectWithAudio(-6.0206, out AudioTrack track, out _);
        project.Settings.MasterGainDb = -6.0206;

        var scope = new AudioPlanScope(OnlyTrack: track, UnityTrackGain: true, UnityMasterGain: true);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, project.ActiveSequence, Timecode.Zero, Timecode.FromSeconds(0.1), scope);
        Assert.Equal(1.0, Assert.Single(plan.Layers).GainStartLinear, 6); // track gain forced to unity
        Assert.Equal(1.0, plan.MasterGainLinear, 6);                       // master gain forced to unity
    }

    [Fact]
    public void Centre_Pan_Leaves_Both_Channels_At_Unity()
    {
        Project project = ProjectWithAudio(0, out _, out _);
        AudioLayer layer = Assert.Single(RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1)).Layers);
        Assert.Equal(1.0, layer.PanLeft, 6);
        Assert.Equal(1.0, layer.PanRight, 6);
    }

    [Fact]
    public void Hard_Right_Pan_Silences_The_Left_Channel()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track, out _);
        track.Pan = 1.0;
        AudioLayer layer = Assert.Single(RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1)).Layers);
        Assert.Equal(0.0, layer.PanLeft, 6);
        Assert.Equal(1.0, layer.PanRight, 6);
    }
}

/// <summary>The stereo balance law (PLAN.md step 30).</summary>
public class PanLawTests
{
    [Fact]
    public void Centre_Is_Unity_On_Both()
    {
        (double l, double r) = PanLaw.Balance(0);
        Assert.Equal(1.0, l, 6);
        Assert.Equal(1.0, r, 6);
    }

    [Theory]
    [InlineData(-1.0, 1.0, 0.0)] // hard left  → right silent
    [InlineData(1.0, 0.0, 1.0)]  // hard right → left silent
    [InlineData(-0.5, 1.0, 0.5)] // half left  → right at 0.5
    public void Panning_Attenuates_The_Opposite_Channel(double pan, double expectedLeft, double expectedRight)
    {
        (double l, double r) = PanLaw.Balance(pan);
        Assert.Equal(expectedLeft, l, 6);
        Assert.Equal(expectedRight, r, 6);
    }

    [Fact]
    public void Out_Of_Range_Is_Clamped()
    {
        Assert.Equal(PanLaw.Balance(1.0), PanLaw.Balance(5.0));
        Assert.Equal(PanLaw.Balance(-1.0), PanLaw.Balance(-5.0));
    }
}

/// <summary>Pure loudness-normalization gain math (PLAN.md step 30).</summary>
public class LoudnessNormalizationTests
{
    [Fact]
    public void Quiet_Signal_Is_Turned_Up_To_Target()
    {
        double gain = LoudnessNormalization.ComputeGainDb(-20.0, double.NegativeInfinity, -14.0);
        Assert.Equal(6.0, gain, 6);
    }

    [Fact]
    public void Loud_Signal_Is_Turned_Down_To_Target()
    {
        double gain = LoudnessNormalization.ComputeGainDb(-10.0, double.NegativeInfinity, -14.0);
        Assert.Equal(-4.0, gain, 6);
    }

    [Fact]
    public void Silence_Is_Left_Alone()
    {
        Assert.Equal(0.0, LoudnessNormalization.ComputeGainDb(double.NegativeInfinity, -3.0, -14.0), 6);
    }

    [Fact]
    public void True_Peak_Ceiling_Caps_The_Boost()
    {
        // Want +6 to reach target, but only +1 of true-peak head-room to the -1 dBTP ceiling.
        double gain = LoudnessNormalization.ComputeGainDb(-20.0, -2.0, -14.0, truePeakCeilingDbtp: -1.0);
        Assert.Equal(1.0, gain, 6);
    }

    [Fact]
    public void True_Peak_Ceiling_Forces_A_Cut_When_Already_Over()
    {
        // Quiet but already 1 dB over the ceiling → must be cut to -1 dBTP even though loudness says boost.
        double gain = LoudnessNormalization.ComputeGainDb(-20.0, 0.0, -14.0, truePeakCeilingDbtp: -1.0);
        Assert.Equal(-1.0, gain, 6);
    }
}
