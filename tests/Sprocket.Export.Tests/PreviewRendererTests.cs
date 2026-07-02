using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// The render cache's intermediates (PLAN.md step 32): <see cref="PreviewRenderer.RenderVideo"/> produces a
/// decodable video-only all-intra file with exactly the range's frames, and
/// <see cref="PreviewRenderer.RenderAudio"/> a float32 WAV of the master mix — both through the same
/// deterministic offline pipeline export uses. Real FFmpeg encode/decode round-trips (fixture on the ffmpeg CLI).
/// </summary>
public class PreviewRendererTests
{
    [Fact]
    public void RenderVideo_Produces_A_Decodable_Video_Only_Intermediate_With_The_Range_Frames()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        using var output = new TempFile(PreviewRenderer.VideoExtension);

        // Half the 1 s / 30 fps fixture.
        var range = new ExportRange(Timecode.Zero, Timecode.FromSeconds(0.5));
        PreviewRenderer.RenderVideo(project, sequenceId: null, range, output.Path);

        Assert.True(File.Exists(output.Path));
        Assert.Equal(15, ExportProbe.CountVideoFrames(output.Path));
        Assert.True(ExportProbe.FirstFrameMeanRgb(output.Path) > 1.0); // real content, not black

        // Video only: the audio side of the cache is separate PCM, so the intermediate has no audio stream
        // even though the project has an audible audio track.
        using MediaSource source = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.False(source.Info.HasAudio);
    }

    [Fact]
    public void RenderVideo_Intermediate_Is_All_Intra()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);
        using var output = new TempFile(PreviewRenderer.VideoExtension);
        PreviewRenderer.RenderVideo(
            project, sequenceId: null, new ExportRange(Timecode.Zero, Timecode.FromSeconds(0.5)), output.Path);

        // GOP 1 means every frame is independently decodable: a mid-file seek must land immediately (an
        // inter-coded file at the fixture's g=12 would have to land on a keyframe well before the target).
        using MediaSource source = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        source.SeekTo(Timecode.FromSeconds(0.4));
        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
            Assert.True(frame!.Pts >= Timecode.FromSeconds(0.3), $"landed at {frame.Pts.ToSeconds():0.###}s — not all-intra?");
    }

    [Fact]
    public void RenderAudio_Produces_The_Master_Mix_As_Float32_Wav()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        using var output = new TempFile(PreviewRenderer.AudioExtension);

        var range = new ExportRange(Timecode.Zero, Timecode.FromSeconds(1));
        PreviewRenderer.RenderAudio(project, sequenceId: null, range, output.Path);

        using WavePcmReader reader = WavePcmReader.Open(output.Path);
        Assert.Equal(ExportFixture.SampleRate, reader.SampleRate);
        Assert.Equal(2, reader.Channels);
        Assert.Equal(ExportFixture.SampleRate, reader.TotalFrames); // exactly the 1 s range

        // The fixture carries a 440 Hz tone — the mixed cache must be audibly non-silent.
        float[] buffer = new float[4800 * 2];
        Assert.Equal(4800, reader.Read(buffer));
        Assert.True(buffer.Max(MathF.Abs) > 0.05f, "cached mix is silent");
    }

    [Fact]
    public void A_Cancelled_Render_Leaves_No_File_Behind()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        using var video = new TempFile(PreviewRenderer.VideoExtension);
        using var audio = new TempFile(PreviewRenderer.AudioExtension);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var range = new ExportRange(Timecode.Zero, Timecode.FromSeconds(1));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PreviewRenderer.RenderVideo(project, null, range, video.Path, cancellationToken: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PreviewRenderer.RenderAudio(project, null, range, audio.Path, cancellationToken: cts.Token));

        Assert.False(File.Exists(video.Path));
        Assert.False(File.Exists(audio.Path));
    }
}
