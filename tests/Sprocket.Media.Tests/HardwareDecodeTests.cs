using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Hardware-accelerated decode (PLAN step 6). The GPU path is device-dependent, so these tests assert the
/// behaviour that must hold <em>regardless</em> of whether a device is present: software mode always decodes
/// with no device; auto mode decodes whether it engages hardware or falls back; and — crucially — the
/// hardware and software paths produce the <b>same frame timing</b>, so enabling hardware never breaks
/// frame-accuracy. (On a machine with a GPU, auto exercises the real hardware download path here.)
/// </summary>
public class HardwareDecodeTests
{
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;

    private static long[] DecodePtsSequence(HardwareAccelMode mode, int count)
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, mode);
        var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        var pts = new List<long>(count);
        while (pts.Count < count && source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            pts.Add(frame.Pts.Ticks);
            frame.Dispose();
        }
        pool.Dispose();
        return [.. pts];
    }

    [Fact]
    public void Software_Mode_Uses_No_Hardware_Device()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        Assert.Null(source.HardwareDeviceName);
    }

    [Fact]
    public void Software_Mode_Decodes_Frames_In_Order()
    {
        long[] pts = DecodePtsSequence(HardwareAccelMode.Disabled, 10);
        Assert.Equal(10, pts.Length);
        for (int i = 0; i < pts.Length; i++)
            Assert.Equal(i * FrameTicks, pts[i]);
    }

    [Fact]
    public void Auto_Mode_Decodes_Whether_Or_Not_Hardware_Engages()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Auto);
        // HardwareDeviceName is a device name when the GPU path engaged, or null on software fallback —
        // both are valid; what must hold is that decoding works either way.
        var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        bool decoded = source.TryDecodeNextFrame(pool, out VideoFrame? frame);
        frame?.Dispose();
        pool.Dispose();
        Assert.True(decoded);
    }

    [Fact]
    public void Hardware_And_Software_Paths_Produce_Identical_Frame_Timing()
    {
        // Frame-accuracy must not depend on the decode backend. Auto may run on the GPU (then this compares
        // the hardware-download path against software) or fall back (software vs software) — either way the
        // PTS sequence must match exactly.
        long[] software = DecodePtsSequence(HardwareAccelMode.Disabled, 20);
        long[] auto = DecodePtsSequence(HardwareAccelMode.Auto, 20);
        Assert.Equal(software, auto);
    }

    [Fact]
    public void Reports_The_Compiled_Hardware_Types()
    {
        // The bundled FFmpeg is built with hardware support; the list is non-empty on a desktop build.
        Assert.NotEmpty(HardwareDevice.CompiledTypes());
    }

    [Fact]
    public void Platform_Preferred_Types_Are_Defined_For_This_Os()
    {
        IReadOnlyList<Sdcb.FFmpeg.Raw.AVHWDeviceType> preferred = HardwareDevice.PlatformPreferredTypes();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.NotEmpty(preferred);
    }
}
