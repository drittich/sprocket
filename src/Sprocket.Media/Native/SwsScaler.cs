namespace Sprocket.Media.Native;

/// <summary>
/// A reusable libswscale pixel converter (replaces Sdcb's <c>VideoFrameConverter</c>). Lazily creates an
/// <c>SwsContext</c> and recreates it only when the source/destination geometry or pixel format changes —
/// so the steady-state decode/encode path performs no per-frame native allocation (§1, §8).
/// </summary>
internal sealed unsafe class SwsScaler : IDisposable
{
    private IntPtr _ctx;
    private int _sw = -1, _sh = -1, _sfmt = int.MinValue, _dw = -1, _dh = -1, _dfmt = int.MinValue;

    /// <summary>Scales/format-converts <paramref name="src"/> into <paramref name="dst"/> (both native frames).</summary>
    public void Convert(AvFrameHandle src, AvFrameHandle dst)
    {
        EnsureContext(src.Width, src.Height, src.Format, dst.Width, dst.Height, dst.Format);

        byte** srcData = stackalloc byte*[4];
        int* srcStride = stackalloc int[4];
        byte** dstData = stackalloc byte*[4];
        int* dstStride = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            srcData[i] = (byte*)src.Data(i);
            srcStride[i] = src.Linesize(i);
            dstData[i] = (byte*)dst.Data(i);
            dstStride[i] = dst.Linesize(i);
        }
        LibAv.sws_scale(_ctx, srcData, srcStride, 0, src.Height, dstData, dstStride);
    }

    private void EnsureContext(int sw, int sh, int sfmt, int dw, int dh, int dfmt)
    {
        if (_ctx != IntPtr.Zero && sw == _sw && sh == _sh && sfmt == _sfmt && dw == _dw && dh == _dh && dfmt == _dfmt)
            return;

        if (_ctx != IntPtr.Zero) LibAv.sws_freeContext(_ctx);
        _ctx = LibAv.sws_getContext(sw, sh, sfmt, dw, dh, dfmt, AvConst.SwsBilinear, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ctx == IntPtr.Zero)
            throw new FFmpegException("sws_getContext", 0, $"no scaler for {sw}x{sh}/{sfmt} -> {dw}x{dh}/{dfmt}");
        (_sw, _sh, _sfmt, _dw, _dh, _dfmt) = (sw, sh, sfmt, dw, dh, dfmt);
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        LibAv.sws_freeContext(_ctx);
        _ctx = IntPtr.Zero;
    }
}
