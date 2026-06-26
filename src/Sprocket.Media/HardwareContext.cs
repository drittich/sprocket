using Sdcb.FFmpeg.Raw;

namespace Sprocket.Media;

/// <summary>Whether <see cref="MediaSource"/> attempts hardware-accelerated decode.</summary>
public enum HardwareAccelMode
{
    /// <summary>Probe for a platform GPU decoder and use it if available, falling back to software (the default).</summary>
    Auto,

    /// <summary>Always decode in software (deterministic; used by tests and as the guaranteed fallback).</summary>
    Disabled,
}

/// <summary>
/// An FFmpeg hardware device context (ARCHITECTURE.md §11): a created <c>AVHWDeviceContext</c> of one
/// <see cref="AVHWDeviceType"/> (D3D11VA/CUDA/QSV on Windows, VAAPI/CUDA on Linux, VideoToolbox on macOS).
/// <see cref="MediaSource"/> attaches it to a decoder so frames decode on the GPU; the per-OS device
/// selection lives behind this interface so the decode path is identical everywhere and always has a
/// software fallback.
/// </summary>
public interface IHardwareContext : IDisposable
{
    /// <summary>The FFmpeg device type backing this context.</summary>
    AVHWDeviceType DeviceType { get; }

    /// <summary>Human-readable device type name (e.g. <c>"d3d11va"</c>, <c>"videotoolbox"</c>).</summary>
    string Name { get; }

    /// <summary>The underlying <c>AVBufferRef*</c> device context, as a native pointer.</summary>
    nint DeviceContextRef { get; }
}

/// <summary>
/// The concrete <see cref="IHardwareContext"/>: wraps an <c>AVBufferRef*</c> created by
/// <c>av_hwdevice_ctx_create</c>. Creation is a runtime probe — it returns <c>null</c> when the device type
/// is unavailable (no driver/GPU), so callers iterate the platform-preferred list and degrade to software.
/// </summary>
public sealed unsafe class HardwareDevice : IHardwareContext
{
    private AVBufferRef* _ctx;

    private HardwareDevice(AVBufferRef* ctx, AVHWDeviceType type)
    {
        _ctx = ctx;
        DeviceType = type;
        Name = ffmpeg.av_hwdevice_get_type_name(type) ?? type.ToString();
    }

    /// <inheritdoc />
    public AVHWDeviceType DeviceType { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public nint DeviceContextRef => (nint)_ctx;

    /// <summary>Attempts to open a device of <paramref name="type"/>. Returns <c>null</c> if it is unavailable.</summary>
    public static HardwareDevice? TryCreate(AVHWDeviceType type)
    {
        AVBufferRef* ctx = null;
        int rc = ffmpeg.av_hwdevice_ctx_create(&ctx, type, null, null, 0);
        if (rc < 0 || ctx is null)
        {
            if (ctx is not null)
            {
                AVBufferRef* c = ctx;
                ffmpeg.av_buffer_unref(&c);
            }
            return null;
        }
        return new HardwareDevice(ctx, type);
    }

    /// <summary>The hardware device types to try, most-preferred first, for the current OS (ARCHITECTURE.md §11).</summary>
    public static IReadOnlyList<AVHWDeviceType> PlatformPreferredTypes()
    {
        if (OperatingSystem.IsWindows())
            return [AVHWDeviceType.D3d11va, AVHWDeviceType.Cuda, AVHWDeviceType.Qsv, AVHWDeviceType.Dxva2];
        if (OperatingSystem.IsMacOS())
            return [AVHWDeviceType.Videotoolbox];
        if (OperatingSystem.IsLinux())
            return [AVHWDeviceType.Vaapi, AVHWDeviceType.Cuda, AVHWDeviceType.Vdpau];
        return [];
    }

    /// <summary>Every hardware device type this FFmpeg build was compiled with (diagnostics).</summary>
    public static IReadOnlyList<AVHWDeviceType> CompiledTypes()
    {
        var types = new List<AVHWDeviceType>();
        AVHWDeviceType t = AVHWDeviceType.None;
        while ((t = ffmpeg.av_hwdevice_iterate_types(t)) != AVHWDeviceType.None)
            types.Add(t);
        return types;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ctx is null)
            return;
        AVBufferRef* c = _ctx;
        ffmpeg.av_buffer_unref(&c);
        _ctx = null;
    }
}
