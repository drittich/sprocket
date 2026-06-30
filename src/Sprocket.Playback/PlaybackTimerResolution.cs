using System.Runtime.InteropServices;

namespace Sprocket.Playback;

/// <summary>
/// Raises the Windows multimedia timer resolution to 1&#160;ms for the duration of playback so the pump's
/// <c>Task.Delay</c> frame-pacing is honoured accurately (ARCHITECTURE.md §8).
/// </summary>
/// <remarks>
/// Windows' default scheduler tick is ~15.6&#160;ms, so an unraised <c>await Task.Delay(16)</c> actually sleeps
/// ~31&#160;ms — turning a 30&#160;fps frame-pace into an irregular ~32&#160;Hz poll that aliases against the true
/// 33.3&#160;ms frame grid and produces visible judder even though no frames are dropped (measured: present-interval
/// sd ≈ 10&#160;ms, gaps up to ~63&#160;ms). <c>timeBeginPeriod(1)</c> drops that to ~1&#160;ms. It is a process-wide,
/// reference-counted setting with a small power cost, so it is held <b>only while playing</b> (raised on the
/// transition into <see cref="PlaybackState.Playing"/>, lowered on leaving it / disposal) and every
/// <see cref="Raise"/> is balanced by one <see cref="Lower"/>. No-op on non-Windows, where the timer is already
/// ~1&#160;ms.
/// </remarks>
internal static partial class PlaybackTimerResolution
{
    private const uint PeriodMs = 1;

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint uPeriod);

    /// <summary>Requests the 1&#160;ms timer period (Windows only). Balance with one <see cref="Lower"/>.</summary>
    public static void Raise()
    {
        if (OperatingSystem.IsWindows())
            timeBeginPeriod(PeriodMs);
    }

    /// <summary>Releases a prior <see cref="Raise"/> (Windows only).</summary>
    public static void Lower()
    {
        if (OperatingSystem.IsWindows())
            timeEndPeriod(PeriodMs);
    }
}
