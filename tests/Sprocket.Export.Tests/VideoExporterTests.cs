using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Exercises the full export pipeline end-to-end (PLAN.md step 8 / slice DoD #7): render the timeline through
/// the render graph + Skia effect shaders, encode H.264 + AAC, mux to MP4, then reopen the result with the
/// decode path and assert its properties. These are real encode→decode round-trips (libx264 + AAC), so they
/// need the FFmpeg + SkiaSharp natives the csproj pulls in.
/// </summary>
public sealed class VideoExporterTests
{
    [Fact]
    public void Export_ProducesPlayableMp4_WithMatchingFormat()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        Assert.True(File.Exists(output.Path));
        Assert.True(new FileInfo(output.Path).Length > 0);

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        ProbedMediaInfo info = decoded.Info;

        Assert.True(info.HasVideo);
        Assert.Equal(ExportFixture.Width, info.Width);
        Assert.Equal(ExportFixture.Height, info.Height);
        Assert.True(info.HasAudio);

        // ~30 fps and ~1 s. The container reports an *averaged* frame rate, which on a 1-second clip carries
        // a frame's worth of imprecision (30 frames over ~0.97 s ≈ 31 fps), so the bound is deliberately wide.
        double fps = (double)info.FrameRate.Num / info.FrameRate.Den;
        Assert.InRange(fps, 28.0, 33.0);
        Assert.InRange(info.Duration.ToSeconds(), 0.8, 1.3);
    }

    [Fact]
    public void Export_RendersTheFullFrameCount()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        int frames = CountVideoFrames(output.Path);
        // 1 s at 30 fps ≈ 30 frames; allow a small tolerance for encoder/container edge frames.
        Assert.InRange(frames, 28, 32);
    }

    [Fact]
    public void Export_AppliesBrightnessEffectOnTheExportPath()
    {
        // The same render graph + effect shaders run for export, so a brightness-down clip must produce a
        // visibly darker first frame than an unmodified one.
        using var dark = new TempFile();
        using var plain = new TempFile();
        VideoExporter.Export(ExportFixture.BuildProject(withAudio: false, brightness: 0.3), dark.Path);
        VideoExporter.Export(ExportFixture.BuildProject(withAudio: false, brightness: 1.0), plain.Path);

        double darkMean = FirstFrameMeanRgb(dark.Path);
        double plainMean = FirstFrameMeanRgb(plain.Path);

        Assert.True(darkMean < plainMean * 0.6,
            $"Brightness 0.3 should darken the frame: dark={darkMean:0.0}, plain={plainMean:0.0}");
    }

    [Fact]
    public void Export_WithoutAudioTracks_WritesVideoOnlyFile()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.True(decoded.Info.HasVideo);
        Assert.False(decoded.Info.HasAudio);
    }

    [Fact]
    public void Export_ReportsProgressToCompletion()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        var reported = new List<double>();

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path, progress: new Progress<double>(reported.Add));

        // Progress is delivered via Progress<T> (posts to the thread pool here, since there is no sync context),
        // so just assert it eventually hit completion.
        Assert.Contains(reported, p => p >= 0.99);
    }

    [Fact]
    public void Export_EmptyTimeline_Throws()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(320, 240), 48000);
        var project = new Project(timeline);

        using var output = new TempFile();
        Assert.Throws<ArgumentException>(() => VideoExporter.Export(project, output.Path));
    }

    private static int CountVideoFrames(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        int count = 0;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            using (frame)
                count++;
        }
        return count;
    }

    /// <summary>Mean of the R/G/B channels of the first decoded frame (0–255), as a brightness proxy.</summary>
    private static unsafe double FirstFrameMeanRgb(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
        {
            var p = (byte*)frame.Pixels;
            int rowBytes = frame.RowBytes;
            long sum = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                byte* row = p + (long)y * rowBytes;
                for (int x = 0; x < frame.Width; x++)
                {
                    byte* px = row + x * 4; // RGBA
                    sum += px[0] + px[1] + px[2];
                }
            }
            return (double)sum / (frame.Width * (long)frame.Height * 3);
        }
    }

    /// <summary>A scratch .mp4 path that deletes itself on dispose.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-export-{Guid.NewGuid():N}.mp4");

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
