using System.Runtime.InteropServices;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>Whether <see cref="MediaSource"/> attempts hardware-accelerated decode.</summary>
public enum HardwareAccelMode
{
    /// <summary>Probe for a platform GPU decoder and use it if available, falling back to software (the default).</summary>
    Auto,

    /// <summary>Always decode in software (deterministic; used by tests and as the guaranteed fallback).</summary>
    Disabled,
}

/// <summary>The FFmpeg <c>AVHWDeviceType</c> values Sprocket targets (numeric values match FFmpeg's enum).
/// Replaces the binding-specific enum so no FFmpeg type leaks across the Media public surface.</summary>
public enum HardwareDeviceType
{
    None = 0,
    Vdpau = 1,
    Cuda = 2,
    Vaapi = 3,
    Dxva2 = 4,
    Qsv = 5,
    VideoToolbox = 6,
    D3D11Va = 7,
    Drm = 8,
    OpenCl = 9,
    MediaCodec = 10,
    Vulkan = 11,
    D3D12Va = 12,
}

/// <summary>
/// An FFmpeg hardware device context (ARCHITECTURE.md §11): a created <c>AVHWDeviceContext</c> of one
/// <see cref="HardwareDeviceType"/> (D3D11VA/CUDA/QSV on Windows, VAAPI/CUDA on Linux, VideoToolbox on macOS).
/// <see cref="MediaSource"/> attaches it to a decoder so frames decode on the GPU; the per-OS device
/// selection lives behind this interface so the decode path is identical everywhere and always has a
/// software fallback.
/// </summary>
public interface IHardwareContext : IDisposable
{
    /// <summary>The FFmpeg device type backing this context.</summary>
    HardwareDeviceType DeviceType { get; }

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
    private IntPtr _ctx; // AVBufferRef*

    private HardwareDevice(IntPtr ctx, HardwareDeviceType type)
    {
        _ctx = ctx;
        DeviceType = type;
        Name = TypeName((int)type) ?? type.ToString();
    }

    /// <inheritdoc />
    public HardwareDeviceType DeviceType { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public nint DeviceContextRef => _ctx;

    /// <summary>Attempts to open a device of <paramref name="type"/>. Returns <c>null</c> if it is unavailable.</summary>
    public static HardwareDevice? TryCreate(HardwareDeviceType type)
    {
        int rc = LibAv.av_hwdevice_ctx_create(out IntPtr ctx, (int)type, null, IntPtr.Zero, 0);
        if (rc < 0 || ctx == IntPtr.Zero)
        {
            if (ctx != IntPtr.Zero)
                LibAv.av_buffer_unref(ref ctx);
            return null;
        }
        return new HardwareDevice(ctx, type);
    }

    /// <summary>The hardware device types to try, most-preferred first, for the current OS (ARCHITECTURE.md §11).</summary>
    public static IReadOnlyList<HardwareDeviceType> PlatformPreferredTypes()
    {
        if (OperatingSystem.IsWindows())
            return [HardwareDeviceType.D3D11Va, HardwareDeviceType.Cuda, HardwareDeviceType.Qsv, HardwareDeviceType.Dxva2];
        if (OperatingSystem.IsMacOS())
            return [HardwareDeviceType.VideoToolbox];
        if (OperatingSystem.IsLinux())
            return [HardwareDeviceType.Vaapi, HardwareDeviceType.Cuda, HardwareDeviceType.Vdpau];
        return [];
    }

    /// <summary>Every hardware device type this FFmpeg build was compiled with (diagnostics).</summary>
    public static IReadOnlyList<HardwareDeviceType> CompiledTypes()
    {
        var types = new List<HardwareDeviceType>();
        int t = AvConst.HwTypeNone;
        while ((t = LibAv.av_hwdevice_iterate_types(t)) != AvConst.HwTypeNone)
            types.Add((HardwareDeviceType)t);
        return types;
    }

    private static string? TypeName(int type)
    {
        IntPtr p = LibAv.av_hwdevice_get_type_name(type);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ctx == IntPtr.Zero)
            return;
        LibAv.av_buffer_unref(ref _ctx);
        _ctx = IntPtr.Zero;
    }
}
