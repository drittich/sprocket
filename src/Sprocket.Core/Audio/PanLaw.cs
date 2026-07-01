namespace Sprocket.Core.Audio;

/// <summary>
/// The stereo balance law (PLAN.md step 30) that turns an <see cref="Model.AudioTrack.Pan"/> value in [-1, 1] into
/// per-channel linear gains. A linear balance: the centre (0) leaves both channels at unity — so a centred track
/// mixes exactly as it did before pan existed — and panning attenuates the opposite channel to silence at the
/// extreme (−1 → left only, +1 → right only).
/// </summary>
public static class PanLaw
{
    /// <summary>The (left, right) linear gains for a pan/balance value in [-1, 1] (values outside are clamped).</summary>
    public static (double Left, double Right) Balance(double pan)
    {
        pan = Math.Clamp(pan, -1.0, 1.0);
        double left = pan <= 0.0 ? 1.0 : 1.0 - pan;
        double right = pan >= 0.0 ? 1.0 : 1.0 + pan;
        return (left, right);
    }
}
