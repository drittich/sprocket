using System;
using System.Diagnostics;
using System.IO;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// Wires up the slice's playback session: opens a media file, builds a one-video-track
/// <see cref="Project"/> over it, and hands back a started <see cref="PlaybackEngine"/>. Real media import
/// (a bin, drag-drop, dialogs) is a later milestone (PLAN steps 11/15); for the slice the app opens the
/// path given on the command line, or generates a sample clip.
/// </summary>
internal static class MediaBootstrap
{
    /// <summary>The created engine and a human-readable status line, or a null engine and an error message.</summary>
    public readonly record struct Result(PlaybackEngine? Engine, string Status);

    /// <summary>Opens <c>args[0]</c> (if it is an existing file) or a generated sample, and builds the engine.</summary>
    public static Result Create(string[] args)
    {
        try
        {
            string path = args.Length > 0 && File.Exists(args[0]) ? args[0] : SampleClip.EnsureExists();

            MediaSource source = MediaSource.Open(path);
            ProbedMediaInfo info = source.Info;

            int sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
            var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
            var project = new Project(timeline);

            var mediaId = MediaRefId.New();
            project.MediaPool.Add(new MediaRef(mediaId, path, info));

            var track = new VideoTrack { Name = "V1" };
            var videoClip = new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero);
            // Slice DoD #4/#5: a GPU brightness effect plus a fade in/out, both as SkSL shaders (step 7).
            videoClip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.15));
            videoClip.Effects.Add(new EffectInstance(EffectTypeIds.Fade)
                .Set(EffectParamNames.Opacity, FadeInOut(info.Duration)));
            track.Clips.Add(videoClip);
            timeline.Tracks.Add(track);

            // Master clock: the audio device clock when the source has audio and a device is available
            // (audio is the master, ARCHITECTURE.md §8); otherwise the software wall-clock (video-only).
            (IMasterClock? clock, bool audioWired) = TryCreateAudioClock(project, mediaId, path, sampleRate);

            var feed = new RingVideoFrameFeed(new VideoDecodeRing(source));
            var engine = new PlaybackEngine(project, feed, clock); // engine owns + disposes the clock
            engine.Start();

            string status =
                $"{Path.GetFileName(path)}  ·  {info.Width}×{info.Height}  ·  " +
                $"{Fps(info.FrameRate):0.##} fps  ·  {info.Duration.ToSeconds():0.0}s  ·  " +
                (audioWired ? "audio master clock" : "no audio (software clock)");
            return new Result(engine, status);
        }
        catch (Exception ex)
        {
            return new Result(null, $"Could not open media: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the audio track + audio master clock for the source, or returns <c>(null, false)</c> so the
    /// engine falls back to its default <c>SoftwareClock</c> — when the source has no audio, or no audio device
    /// is available. Failures degrade gracefully (ARCHITECTURE.md §15): a missing device must not stop playback.
    /// </summary>
    private static (IMasterClock? clock, bool audioWired) TryCreateAudioClock(
        Project project, MediaRefId mediaId, string path, int sampleRate)
    {
        if (project.MediaPool.Get(mediaId) is not { Info.HasAudio: true })
            return (null, false);

        const int channels = 2; // stereo output; the source is upmixed/downmixed at decode
        AudioSource? audio = null;
        OpenAlAudioOutput? output = null;
        try
        {
            audio = AudioSource.Open(path, sampleRate, channels);

            var audioTrack = new AudioTrack { Name = "A1" };
            var audioClip = new Clip(mediaId, Timecode.Zero, project.Timeline.Duration, Timecode.Zero);
            // The same fade envelope drives audio gain in the mixer (§6) as drives video alpha (§7).
            audioClip.Effects.Add(new EffectInstance(EffectTypeIds.Fade)
                .Set(EffectParamNames.Opacity, FadeInOut(project.Timeline.Duration)));
            audioTrack.Clips.Add(audioClip);
            project.Timeline.Tracks.Add(audioTrack);

            var mixer = new AudioMixer(sampleRate, channels, id => id == mediaId ? audio : null);

            output = new OpenAlAudioOutput();
            output.Configure(sampleRate, channels);

            // The engine takes ownership: it disposes the mixer (which disposes the AudioSource) and the output.
            return (new AudioEngine(output, mixer, project), true);
        }
        catch
        {
            output?.Dispose();
            audio?.Dispose();
            return (null, false); // degrade to the software clock; video still plays
        }
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;

    /// <summary>
    /// A fade-in over the first second and fade-out over the last second of a clip of length
    /// <paramref name="duration"/> (opacity 0→1 … 1→0). Degrades to a plain fade-in/out pair for very short
    /// clips. The opacity drives both video alpha (shader) and audio gain (mixer) so the fade is consistent.
    /// </summary>
    private static AnimatableValue FadeInOut(Timecode duration)
    {
        // Keep the ramps short relative to the clip so a tiny clip still gets a (degenerate) fade.
        Timecode ramp = Timecode.Min(Timecode.FromSeconds(1), new Timecode(duration.Ticks / 2));
        return AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 0.0, Interpolation.Linear),
            new Keyframe(ramp, 1.0, Interpolation.Linear),
            new Keyframe(duration - ramp, 1.0, Interpolation.Linear),
            new Keyframe(duration, 0.0, Interpolation.Linear),
        ]);
    }
}

/// <summary>
/// Ensures a 1080p sample clip exists to play when no file is given on the command line. Generated once with
/// the <c>ffmpeg</c> CLI into the app's output directory and cached (mirrors the spike's test asset).
/// </summary>
internal static class SampleClip
{
    public static string EnsureExists()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets");
        string path = Path.Combine(dir, "sample.mp4");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("ffmpeg",
            "-y -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=6 " +
            "-f lavfi -i sine=frequency=440:sample_rate=48000:duration=6 " +
            $"-c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process? p = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "No media path given and the ffmpeg CLI was not found to generate a sample clip. " +
                "Pass a video file path as the first argument.");
        p.WaitForExit();

        if (!File.Exists(path))
            throw new InvalidOperationException("ffmpeg failed to generate the sample clip.");
        return path;
    }
}
