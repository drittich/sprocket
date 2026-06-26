using System;
using System.Linq;
using Sprocket.App.MediaBrowser;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the media browser's pure helpers (PLAN.md step 15): badge derivation, the waveform
/// peak reduction, and the search filter. The thumbnail decode + the panel's rendering rest on these plus
/// manual verification (the App is a UI-bound WinExe), mirroring the step-12 TimelineMath split.
/// </summary>
public class MediaBrowserTests
{
    private static ProbedMediaInfo Video(int w, int h, double seconds, bool audio = true) =>
        new(Timecode.FromSeconds(seconds), HasVideo: true, new Rational(30, 1), w, h, audio, audio ? 48000 : 0, audio ? 2 : 0);

    private static ProbedMediaInfo AudioOnly(double seconds) =>
        new(Timecode.FromSeconds(seconds), HasVideo: false, Rational.Zero, 0, 0, HasAudio: true, 48000, 2);

    // ── MediaBadges ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3840, 2160, "4K")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(640, 480, "640×480")]
    public void ResolutionTier_Buckets_By_Height(int w, int h, string expected) =>
        Assert.Equal(expected, MediaBadges.ResolutionTier(w, h));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(22.4, "0:22")]
    [InlineData(96, "1:36")]
    public void Duration_Formats_As_Minutes_Seconds(double seconds, string expected) =>
        Assert.Equal(expected, MediaBadges.Duration(seconds));

    [Theory]
    [InlineData("C:/clips/Ambient_Score.aif", "AIF")]
    [InlineData("song.wav", "WAV")]
    [InlineData("noext", "AUDIO")]
    public void FormatTag_Uppercases_The_Extension(string path, string expected) =>
        Assert.Equal(expected, MediaBadges.FormatTag(path));

    [Fact]
    public void Describe_Video_Shows_Duration_And_Resolution()
    {
        var badges = MediaBadges.Describe(Video(3840, 2160, 22), "Interview_A.mp4");
        Assert.Equal(new[] { "0:22", "4K" }, badges);
    }

    [Fact]
    public void Describe_Audio_Shows_Duration_And_Format()
    {
        var badges = MediaBadges.Describe(AudioOnly(38), "Ambient_Score.aif");
        Assert.Equal(new[] { "0:38", "AIF" }, badges);
    }

    // ── WaveformBuilder ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPeaks_Returns_Requested_Bucket_Count()
    {
        float[] peaks = WaveformBuilder.BuildPeaks(new float[2000], channels: 1, bucketCount: 64);
        Assert.Equal(64, peaks.Length);
    }

    [Fact]
    public void BuildPeaks_Captures_The_Bucket_Peak_In_Range()
    {
        // 100 mono frames: a single loud sample in the first half, quiet in the second.
        var samples = new float[100];
        samples[10] = 0.8f;
        float[] peaks = WaveformBuilder.BuildPeaks(samples, channels: 1, bucketCount: 2);

        Assert.Equal(0.8f, peaks[0], 3);   // first bucket peak
        Assert.Equal(0f, peaks[1], 3);     // second bucket is silent
        Assert.All(peaks, p => Assert.InRange(p, 0f, 1f));
    }

    [Fact]
    public void BuildPeaks_Mono_Mixes_Stereo_Frames()
    {
        // One stereo frame: L=+1, R=-1 → mono mix 0; |0| = 0 peak.
        float[] peaks = WaveformBuilder.BuildPeaks(new[] { 1f, -1f }, channels: 2, bucketCount: 1);
        Assert.Equal(0f, peaks[0], 5);

        // L=+1, R=+1 → mono 1.
        peaks = WaveformBuilder.BuildPeaks(new[] { 1f, 1f }, channels: 2, bucketCount: 1);
        Assert.Equal(1f, peaks[0], 5);
    }

    [Fact]
    public void BuildPeaks_Empty_Is_All_Zero()
    {
        float[] peaks = WaveformBuilder.BuildPeaks(Array.Empty<float>(), channels: 2, bucketCount: 8);
        Assert.Equal(8, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void BuildPeaks_Rejects_Bad_Arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformBuilder.BuildPeaks(new float[4], channels: 0, bucketCount: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformBuilder.BuildPeaks(new float[4], channels: 1, bucketCount: 0));
    }

    // ── MediaSearch ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Interview_A.mp4", "", true)]
    [InlineData("Interview_A.mp4", "   ", true)]
    [InlineData("Interview_A.mp4", "view", true)]
    [InlineData("Interview_A.mp4", "VIEW", true)]
    [InlineData("Interview_A.mp4", "broll", false)]
    public void Matches_Is_Case_Insensitive_Substring(string text, string query, bool expected) =>
        Assert.Equal(expected, MediaSearch.Matches(text, query));

    [Fact]
    public void Matches_Empty_Text_Only_Matches_Empty_Query()
    {
        Assert.True(MediaSearch.Matches("", ""));
        Assert.False(MediaSearch.Matches("", "x"));
    }
}
