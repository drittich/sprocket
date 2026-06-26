using System.Diagnostics;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Export.Tests;

/// <summary>
/// Generates (once) the small deterministic source clip the export tests render from — 320×240@30 for 1 s
/// (30 frames) with a 48 kHz audio tone — and builds <see cref="Project"/>s over it. Mirrors the Media-layer
/// fixture; built with the <c>ffmpeg</c> CLI into the test output dir and cached.
/// </summary>
internal static class ExportFixture
{
    public const int Width = 320;
    public const int Height = 240;
    public const int Fps = 30;
    public const int SampleRate = 48000;

    private static readonly Lazy<string> LazyPath = new(Generate);

    /// <summary>Absolute path to the source fixture clip, generating it on first use.</summary>
    public static string SourcePath => LazyPath.Value;

    /// <summary>Probes the fixture's stream info (opens + disposes a source).</summary>
    public static ProbedMediaInfo Probe()
    {
        using MediaSource source = MediaSource.Open(SourcePath, HardwareAccelMode.Disabled);
        return source.Info;
    }

    /// <summary>
    /// Builds a one-video-track (optionally one-audio-track) project over the fixture. When
    /// <paramref name="brightness"/> is given, a Brightness effect with that amount is applied to the video clip.
    /// </summary>
    public static Project BuildProject(bool withAudio = true, double? brightness = null)
    {
        ProbedMediaInfo info = Probe();
        int sampleRate = info.SampleRate > 0 ? info.SampleRate : SampleRate;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
        var project = new Project(timeline);

        var mediaId = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(mediaId, SourcePath, info));

        var videoTrack = new VideoTrack { Name = "V1" };
        var videoClip = new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero);
        if (brightness is { } amount)
            videoClip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, amount));
        videoTrack.Clips.Add(videoClip);
        timeline.Tracks.Add(videoTrack);

        if (withAudio && info.HasAudio)
        {
            var audioTrack = new AudioTrack { Name = "A1" };
            audioTrack.Clips.Add(new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero));
            timeline.Tracks.Add(audioTrack);
        }

        return project;
    }

    private static string Generate()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "export-source.mp4");
        if (File.Exists(path))
            return path;

        string args =
            "-y " +
            $"-f lavfi -i testsrc2=size={Width}x{Height}:rate={Fps}:duration=1 " +
            $"-f lavfi -i sine=frequency=440:sample_rate={SampleRate}:duration=1 " +
            "-c:v libx264 -g 12 -pix_fmt yuv420p -c:a aac -shortest " +
            $"\"{path}\"";

        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg CLI not found on PATH to generate the export fixture.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!File.Exists(path))
            throw new InvalidOperationException($"ffmpeg failed to generate the export fixture.\n{stderr}");
        return path;
    }
}
