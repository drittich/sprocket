using Sprocket.App.RenderCache;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>The render bar's span computation (PLAN.md step 32): red over nested sequences / transitions,
/// yellow over un-rendered effect-bearing content, green wherever a valid cached render covers — pure and
/// headless, matching what the timeline strip draws.</summary>
public class RenderBarModelTests
{
    private static Timeline NewTimeline() => new(new Rational(30, 1), new Resolution(1920, 1080), 48000);

    private static Clip MediaClip(double startSec, double durationSec)
        => new(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(durationSec), Timecode.FromSeconds(startSec));

    [Fact]
    public void A_Plain_Clip_Produces_No_Span()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(MediaClip(0, 5));
        timeline.Tracks.Add(track);

        Assert.Empty(RenderBarModel.Compute(timeline, []));
    }

    [Fact]
    public void An_Effect_Clip_Is_Yellow_And_A_Nested_Clip_Is_Red()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1" };
        Clip withEffect = MediaClip(0, 2);
        withEffect.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5));
        track.Clips.Add(withEffect);
        track.Clips.Add(Clip.CreateSequenceClip(SequenceId.New(), Timecode.FromSeconds(2), Timecode.FromSeconds(4)));
        timeline.Tracks.Add(track);

        List<RenderBarSpan> spans = RenderBarModel.Compute(timeline, []);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new RenderBarSpan(0, Timecode.FromSeconds(2).Ticks, RenderBarState.NeedsRender), spans[0]);
        Assert.Equal(
            new RenderBarSpan(Timecode.FromSeconds(4).Ticks, Timecode.FromSeconds(6).Ticks, RenderBarState.NeedsRenderHeavy),
            spans[1]);
    }

    [Fact]
    public void A_Transition_Window_Is_Red_And_Wins_Over_Yellow()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1" };
        Clip a = MediaClip(0, 2);
        a.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5));
        track.Clips.Add(a);
        track.Clips.Add(MediaClip(2, 2));
        track.Transitions.Add(new Transition(
            TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(2), Timecode.FromSeconds(1)));
        timeline.Tracks.Add(track);

        List<RenderBarSpan> spans = RenderBarModel.Compute(timeline, []);
        // Yellow up to the transition window, red across it (centre-on-cut: [1.5, 2.5)).
        Assert.Contains(spans, s => s.State == RenderBarState.NeedsRenderHeavy
            && s.InTicks == Timecode.FromSeconds(1.5).Ticks && s.OutTicks == Timecode.FromSeconds(2.5).Ticks);
        Assert.Contains(spans, s => s.State == RenderBarState.NeedsRender && s.OutTicks == Timecode.FromSeconds(1.5).Ticks);
    }

    [Fact]
    public void A_Valid_Cached_Range_Is_Green_And_Overrides_Needs_Render()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(Clip.CreateSequenceClip(SequenceId.New(), Timecode.FromSeconds(4), Timecode.Zero));
        timeline.Tracks.Add(track);

        List<RenderBarSpan> spans = RenderBarModel.Compute(
            timeline, [(Timecode.FromSeconds(1), Timecode.FromSeconds(3))]);

        Assert.Equal(3, spans.Count);
        Assert.Equal(RenderBarState.NeedsRenderHeavy, spans[0].State); // [0, 1)
        Assert.Equal(RenderBarState.Rendered, spans[1].State);        // [1, 3) — the cache wins
        Assert.Equal(RenderBarState.NeedsRenderHeavy, spans[2].State); // [3, 4)
        Assert.Equal(Timecode.FromSeconds(1).Ticks, spans[1].InTicks);
        Assert.Equal(Timecode.FromSeconds(3).Ticks, spans[1].OutTicks);
    }

    [Fact]
    public void A_Disabled_Track_Contributes_Nothing()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1", Enabled = false };
        track.Clips.Add(Clip.CreateSequenceClip(SequenceId.New(), Timecode.FromSeconds(4), Timecode.Zero));
        timeline.Tracks.Add(track);

        Assert.Empty(RenderBarModel.Compute(timeline, []));
    }

    [Fact]
    public void Adjacent_Same_State_Spans_Merge()
    {
        Timeline timeline = NewTimeline();
        var track = new VideoTrack { Name = "V1" };
        Clip a = MediaClip(0, 2);
        a.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5));
        Clip b = MediaClip(2, 2);
        b.Effects.Add(new EffectInstance(EffectTypeIds.Color).Set(EffectParamNames.Saturation, 0.0));
        track.Clips.Add(a);
        track.Clips.Add(b);
        timeline.Tracks.Add(track);

        RenderBarSpan span = Assert.Single(RenderBarModel.Compute(timeline, []));
        Assert.Equal(new RenderBarSpan(0, Timecode.FromSeconds(4).Ticks, RenderBarState.NeedsRender), span);
    }
}
