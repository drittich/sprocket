using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>The render cache's float32-WAV audio intermediate (PLAN.md step 32): a written file round-trips
/// bit-exactly through <see cref="WavePcmReader"/>, seeks are sample-accurate, and an unfinished (cancelled)
/// writer's file is rejected rather than replayed short. Pure managed I/O — no natives.</summary>
public class WavePcmTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    [Fact]
    public void Round_Trips_Samples_Bit_Exactly()
    {
        using var file = new TempFile(".wav");
        float[] samples = new float[Rate * Channels]; // 1 s stereo
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (i % 97 - 48) / 48f;

        using (var writer = new WavePcmWriter(file.Path, Rate, Channels))
        {
            writer.Write(samples);
            writer.Finish();
        }

        using WavePcmReader reader = WavePcmReader.Open(file.Path);
        Assert.Equal(Rate, reader.SampleRate);
        Assert.Equal(Channels, reader.Channels);
        Assert.Equal(Rate, reader.TotalFrames);

        float[] back = new float[samples.Length];
        Assert.Equal(Rate, reader.Read(back));
        Assert.Equal(samples, back);
        Assert.Equal(0, reader.Read(back)); // end of stream
    }

    [Fact]
    public void SeekTo_Is_Sample_Accurate()
    {
        using var file = new TempFile(".wav");
        float[] samples = new float[Rate * Channels];
        for (int frame = 0; frame < Rate; frame++)
            for (int ch = 0; ch < Channels; ch++)
                samples[frame * Channels + ch] = frame; // sample value = its frame index

        using (var writer = new WavePcmWriter(file.Path, Rate, Channels))
        {
            writer.Write(samples);
            writer.Finish();
        }

        using WavePcmReader reader = WavePcmReader.Open(file.Path);
        reader.SeekTo(Timecode.FromSeconds(0.5));
        float[] back = new float[4 * Channels];
        Assert.Equal(4, reader.Read(back));
        Assert.Equal(Rate / 2, back[0]);     // frame 24000
        Assert.Equal(Rate / 2 + 3, back[6]); // frame 24003
    }

    [Fact]
    public void Read_Clamps_At_End_Of_Stream()
    {
        using var file = new TempFile(".wav");
        using (var writer = new WavePcmWriter(file.Path, Rate, Channels))
        {
            writer.Write(new float[10 * Channels]);
            writer.Finish();
        }

        using WavePcmReader reader = WavePcmReader.Open(file.Path);
        reader.SeekTo(Timecode.Zero);
        float[] back = new float[64 * Channels];
        Assert.Equal(10, reader.Read(back)); // short read at EOF, not an error
    }

    [Fact]
    public void An_Unfinished_File_Is_Rejected()
    {
        using var file = new TempFile(".wav");
        using (var writer = new WavePcmWriter(file.Path, Rate, Channels))
        {
            writer.Write(new float[Rate]); // disposed WITHOUT Finish — sizes stay zero (a cancelled render)
        }

        Assert.Throws<InvalidDataException>(() => WavePcmReader.Open(file.Path));
    }

    [Fact]
    public void A_Non_Wav_File_Is_Rejected()
    {
        using var file = new TempFile(".wav");
        File.WriteAllBytes(file.Path, new byte[64]);
        Assert.Throws<InvalidDataException>(() => WavePcmReader.Open(file.Path));
    }
}
