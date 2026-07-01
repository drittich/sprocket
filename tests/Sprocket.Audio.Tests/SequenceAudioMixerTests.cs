using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic tests for mixing a <em>specific</em> sequence rather than the project's active one
/// (PLAN.md step 29 export queue): the export queue can render any sequence, so <see cref="AudioMixer.MixInto"/>
/// gained a sequence-targeting overload. Uses synthetic <see cref="FakePcmReader"/>s (no FFmpeg).
/// </summary>
public class SequenceAudioMixerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static readonly MediaRefId ActiveMedia = MediaRefId.New();
    private static readonly MediaRefId OtherMedia = MediaRefId.New();

    /// <summary>Two sequences: the active one carries a 0.2-valued audio clip, the second a 0.7-valued one.</summary>
    private static (Project project, Sequence other) TwoSequences()
    {
        var activeTimeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var at = new AudioTrack { Name = "A1" };
        at.Clips.Add(new Clip(ActiveMedia, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        activeTimeline.Tracks.Add(at);
        var project = new Project(activeTimeline);

        var otherTimeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var ot = new AudioTrack { Name = "A1" };
        ot.Clips.Add(new Clip(OtherMedia, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        otherTimeline.Tracks.Add(ot);
        var other = new Sequence(SequenceId.New(), "Other", otherTimeline);
        project.Sequences.Add(other);

        return (project, other);
    }

    private static AudioMixer Mixer() => new(Rate, Channels, id =>
        id == ActiveMedia ? new FakePcmReader(Rate, Channels, 0.2f)
        : id == OtherMedia ? new FakePcmReader(Rate, Channels, 0.7f)
        : null);

    [Fact]
    public void MixInto_DefaultOverload_MixesTheActiveSequence()
    {
        using AudioMixer mixer = Mixer();
        (Project project, _) = TwoSequences();
        var buffer = new float[128 * Channels];
        mixer.MixInto(buffer, Timecode.Zero, project);
        Assert.All(buffer, s => Assert.Equal(0.2f, s, 0.0001));
    }

    [Fact]
    public void MixInto_SequenceOverload_MixesTheGivenSequence()
    {
        using AudioMixer mixer = Mixer();
        (Project project, Sequence other) = TwoSequences();
        var buffer = new float[128 * Channels];
        mixer.MixInto(buffer, Timecode.Zero, project, other);
        Assert.All(buffer, s => Assert.Equal(0.7f, s, 0.0001));
    }
}
