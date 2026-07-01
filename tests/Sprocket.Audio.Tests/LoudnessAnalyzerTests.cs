using Sprocket.Audio.Loudness;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic tests for offline loudness measurement (PLAN.md step 30 normalization), using the synthetic
/// <see cref="SinePcmReader"/> (no FFmpeg): a single source (clip scope), a full mix (master scope), and a
/// track-isolating scope (track scope), plus the end-to-end normalize gain math against the measurement.
/// </summary>
public class LoudnessAnalyzerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    [Fact]
    public void MeasureSource_reports_a_finite_loudness_for_a_tone()
    {
        var reader = new SinePcmReader(Rate, Channels, 1000.0, 0.5);
        LoudnessMeasurement m = LoudnessAnalyzer.MeasureSource(reader, Timecode.Zero, Timecode.FromSeconds(3));
        Assert.False(double.IsNegativeInfinity(m.IntegratedLufs));
        Assert.InRange(m.IntegratedLufs, -12.0, -3.0); // ~0.5 full-scale stereo tone
    }

    [Fact]
    public void MeasureSource_doubling_amplitude_is_six_lu_louder()
    {
        double quiet = LoudnessAnalyzer.MeasureSource(
            new SinePcmReader(Rate, Channels, 1000.0, 0.25), Timecode.Zero, Timecode.FromSeconds(3)).IntegratedLufs;
        double loud = LoudnessAnalyzer.MeasureSource(
            new SinePcmReader(Rate, Channels, 1000.0, 0.5), Timecode.Zero, Timecode.FromSeconds(3)).IntegratedLufs;
        Assert.Equal(6.02, loud - quiet, precision: 1);
    }

    [Fact]
    public void MeasureSource_zero_duration_is_silent()
    {
        var reader = new SinePcmReader(Rate, Channels, 1000.0, 0.5);
        LoudnessMeasurement m = LoudnessAnalyzer.MeasureSource(reader, Timecode.Zero, Timecode.Zero);
        Assert.Equal(LoudnessMeasurement.Silent, m);
    }

    // ---- mix / track / master scope -----------------------------------------------------------------------

    private static (Project project, AudioMixer mixer, AudioTrack t1, AudioTrack t2) TwoIdenticalTracks()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate));
        MediaRefId id1 = MediaRefId.New();
        MediaRefId id2 = MediaRefId.New();

        var t1 = new AudioTrack { Name = "A1" };
        t1.Clips.Add(new Clip(id1, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        var t2 = new AudioTrack { Name = "A2" };
        t2.Clips.Add(new Clip(id2, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        project.Timeline.Tracks.Add(t1);
        project.Timeline.Tracks.Add(t2);

        // Two distinct ids resolve to two identical tone readers, so the two layers sum coherently (+6 dB).
        var readers = new Dictionary<MediaRefId, IPcmReader>
        {
            [id1] = new SinePcmReader(Rate, Channels, 1000.0, 0.25),
            [id2] = new SinePcmReader(Rate, Channels, 1000.0, 0.25),
        };
        var mixer = new AudioMixer(Rate, Channels, id => readers.TryGetValue(id, out IPcmReader? r) ? r : null);
        return (project, mixer, t1, t2);
    }

    [Fact]
    public void MeasureMix_two_identical_tracks_are_six_lu_louder_than_one()
    {
        (Project project, AudioMixer mixer, AudioTrack t1, _) = TwoIdenticalTracks();
        Sequence seq = project.ActiveSequence;

        double full = LoudnessAnalyzer.MeasureMix(mixer, project, seq, Timecode.Zero, Timecode.FromSeconds(3)).IntegratedLufs;
        double oneTrack = LoudnessAnalyzer.MeasureMix(
            mixer, project, seq, Timecode.Zero, Timecode.FromSeconds(3),
            new AudioPlanScope(OnlyTrack: t1, UnityTrackGain: true, UnityMasterGain: true)).IntegratedLufs;

        Assert.Equal(6.02, full - oneTrack, precision: 1);
    }

    [Fact]
    public void MeasureMix_unity_master_scope_ignores_master_gain()
    {
        (Project project, AudioMixer mixer, _, _) = TwoIdenticalTracks();
        Sequence seq = project.ActiveSequence;
        project.Settings.MasterGainDb = -6.0206;

        double withMaster = LoudnessAnalyzer.MeasureMix(mixer, project, seq, Timecode.Zero, Timecode.FromSeconds(3)).IntegratedLufs;
        double unity = LoudnessAnalyzer.MeasureMix(
            mixer, project, seq, Timecode.Zero, Timecode.FromSeconds(3),
            new AudioPlanScope(UnityMasterGain: true)).IntegratedLufs;

        Assert.Equal(6.02, unity - withMaster, precision: 1);
    }

    [Fact]
    public void Normalizing_a_measured_source_reaches_the_target()
    {
        var reader = new SinePcmReader(Rate, Channels, 1000.0, 0.25);
        LoudnessMeasurement m = LoudnessAnalyzer.MeasureSource(reader, Timecode.Zero, Timecode.FromSeconds(3));

        // The gain the normalizer would set on the clip, then re-measure a source boosted by that gain.
        double gainDb = LoudnessNormalization.ComputeGainDb(
            m.IntegratedLufs, m.TruePeakDbtp, LoudnessNormalization.BroadcastMinus23Lufs, truePeakCeilingDbtp: 0.0);
        double boostedAmp = 0.25 * Math.Pow(10, gainDb / 20.0);

        var boosted = new SinePcmReader(Rate, Channels, 1000.0, boostedAmp);
        double result = LoudnessAnalyzer.MeasureSource(boosted, Timecode.Zero, Timecode.FromSeconds(3)).IntegratedLufs;

        Assert.Equal(LoudnessNormalization.BroadcastMinus23Lufs, result, precision: 1);
    }
}
