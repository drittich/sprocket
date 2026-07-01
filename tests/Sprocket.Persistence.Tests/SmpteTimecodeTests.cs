using Sprocket.Core.Timing;
using Sprocket.Persistence.Interchange;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>Pure SMPTE timecode conversion used by the EDL exporter (PLAN.md step 28), including the NTSC
/// drop-frame algorithm, verified against known reference values.</summary>
public class SmpteTimecodeTests
{
    private static readonly Rational Fps30 = new(30, 1);
    private static readonly Rational Fps2997 = new(30000, 1001);
    private static readonly Rational Fps5994 = new(60000, 1001);
    private static readonly Rational Fps24 = new(24, 1);
    private static readonly Rational Fps2398 = new(24000, 1001);
    private static readonly Rational Fps25 = new(25, 1);

    [Theory]
    [InlineData(0, "00:00:00:00")]
    [InlineData(29, "00:00:00:29")]
    [InlineData(30, "00:00:01:00")]
    [InlineData(90, "00:00:03:00")]
    [InlineData(1800, "00:01:00:00")]
    public void Formats_NonDrop_30fps(long frame, string expected)
    {
        Assert.Equal(expected, SmpteTimecode.Format(Timecode.FromFrames(frame, Fps30), Fps30));
    }

    [Theory]
    [InlineData(1798, "00:00:59;28")]
    [InlineData(1799, "00:00:59;29")]
    [InlineData(1800, "00:01:00;02")] // drop-frame skips frame numbers ;00 and ;01 at the minute boundary
    public void Formats_DropFrame_2997(long frame, string expected)
    {
        Assert.Equal(expected, SmpteTimecode.Format(Timecode.FromFrames(frame, Fps2997), Fps2997));
    }

    [Fact]
    public void NonDrop_And_DropFrame_Differ_For_NTSC()
    {
        Timecode t = Timecode.FromFrames(1800, Fps2997);
        Assert.Equal("00:01:00:00", SmpteTimecode.Format(t, Fps2997, dropFrame: false));
        Assert.Equal("00:01:00;02", SmpteTimecode.Format(t, Fps2997, dropFrame: true));
    }

    [Fact]
    public void Identifies_Drop_Frame_Rates()
    {
        Assert.True(SmpteTimecode.IsDropFrameRate(Fps2997));
        Assert.True(SmpteTimecode.IsDropFrameRate(Fps5994));
        Assert.False(SmpteTimecode.IsDropFrameRate(Fps30));
        Assert.False(SmpteTimecode.IsDropFrameRate(Fps2398)); // 23.976 is non-drop by convention
        Assert.False(SmpteTimecode.IsDropFrameRate(Fps25));
    }

    [Fact]
    public void Reports_Nominal_Rate()
    {
        Assert.Equal(30, SmpteTimecode.NominalRate(Fps2997));
        Assert.Equal(24, SmpteTimecode.NominalRate(Fps2398));
        Assert.Equal(25, SmpteTimecode.NominalRate(Fps25));
        Assert.Equal(60, SmpteTimecode.NominalRate(Fps5994));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(1798)]
    [InlineData(1800)]
    [InlineData(107892)] // ~1 hour of 29.97
    public void Parses_Back_To_The_Same_Frame_DropFrame(long frame)
    {
        Timecode t = Timecode.FromFrames(frame, Fps2997);
        string s = SmpteTimecode.Format(t, Fps2997, dropFrame: true);
        Timecode parsed = SmpteTimecode.Parse(s, Fps2997, dropFrame: true);
        Assert.Equal(frame, parsed.ToFrameIndex(Fps2997));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(1800)]
    [InlineData(50000)]
    public void Parses_Back_To_The_Same_Frame_NonDrop(long frame)
    {
        Timecode t = Timecode.FromFrames(frame, Fps30);
        string s = SmpteTimecode.Format(t, Fps30);
        Timecode parsed = SmpteTimecode.Parse(s, Fps30);
        Assert.Equal(frame, parsed.ToFrameIndex(Fps30));
    }

    [Fact]
    public void Frame_Code_Mode_Label()
    {
        Assert.Equal("DROP FRAME", SmpteTimecode.FrameCodeMode(Fps2997));
        Assert.Equal("NON-DROP FRAME", SmpteTimecode.FrameCodeMode(Fps30));
    }
}
