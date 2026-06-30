namespace Sprocket.Media.Native;

/// <summary>
/// An owned libswresample context for PCM resample / channel-layout / sample-format conversion. Configured
/// once at open; <see cref="Reinit"/> flushes its internal buffer after a seek. Plane pointers are supplied
/// by the caller, so audio buffers stay native except the one managed PCM crossing the architecture allows.
/// </summary>
internal sealed unsafe class SwrResampler : IDisposable
{
    private IntPtr _swr;

    /// <summary>Allocates and initialises the resampler for the given in/out layout, sample format, and rate.</summary>
    public void Configure(
        AvChannelLayout* outLayout, int outFmt, int outRate,
        AvChannelLayout* inLayout, int inFmt, int inRate)
    {
        IntPtr swr = IntPtr.Zero;
        int rc = LibAv.swr_alloc_set_opts2(out swr, outLayout, outFmt, outRate, inLayout, inFmt, inRate, 0, IntPtr.Zero);
        if (rc < 0 || swr == IntPtr.Zero)
            throw new FFmpegException("swr_alloc_set_opts2", rc, FFmpegError.Describe(rc));
        _swr = swr;
        if (LibAv.swr_init(_swr) is int initRc && initRc < 0)
        {
            LibAv.swr_free(ref _swr);
            throw new FFmpegException("swr_init", initRc, FFmpegError.Describe(initRc));
        }
    }

    /// <summary>Re-initialises the context, discarding any buffered samples (used after a seek).</summary>
    public void Reinit() => FFmpegError.Check(LibAv.swr_init(_swr), "swr_init");

    /// <summary>Upper bound on output sample-frames for <paramref name="inCount"/> input frames (+ buffered).</summary>
    public int GetOutSamples(int inCount) => LibAv.swr_get_out_samples(_swr, inCount);

    /// <summary>Converts (or flushes, when <paramref name="inBuf"/> is null) into <paramref name="outBuf"/>.
    /// Returns sample-frames produced (&lt;0 on error).</summary>
    public int Convert(byte** outBuf, int outCount, byte** inBuf, int inCount)
        => LibAv.swr_convert(_swr, outBuf, outCount, inBuf, inCount);

    public void Dispose()
    {
        if (_swr == IntPtr.Zero) return;
        LibAv.swr_free(ref _swr);
    }
}
