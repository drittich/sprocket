using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>Covers the pure audio-mixer / loudness-meter formatting (PLAN.md step 30, UI.md §3.3): gain and pan
/// labels, LUFS / dBTP read-outs, and the meter fill fraction.</summary>
public class MixerFormatTests
{
    [Theory]
    [InlineData(0.0, "0.0 dB")]
    [InlineData(3.0, "+3.0 dB")]
    [InlineData(-6.0, "-6.0 dB")]
    [InlineData(-60.0, "-∞ dB")]
    [InlineData(double.NegativeInfinity, "-∞ dB")]
    public void GainDbLabel_signs_and_floors(double db, string expected) =>
        Assert.Equal(expected, MixerFormat.GainDbLabel(db));

    [Fact]
    public void GainDbLabel_does_not_render_negative_zero()
    {
        Assert.Equal("0.0 dB", MixerFormat.GainDbLabel(-0.01));
    }

    [Theory]
    [InlineData(0.0, "C")]
    [InlineData(-1.0, "L100")]
    [InlineData(1.0, "R100")]
    [InlineData(-0.5, "L50")]
    [InlineData(0.25, "R25")]
    public void PanLabel_names_the_side(double pan, string expected) =>
        Assert.Equal(expected, MixerFormat.PanLabel(pan));

    [Theory]
    [InlineData(-14.2, "-14.2 LUFS")]
    [InlineData(double.NegativeInfinity, "-∞ LUFS")]
    public void LufsLabel_formats(double lufs, string expected) => Assert.Equal(expected, MixerFormat.LufsLabel(lufs));

    [Theory]
    [InlineData(-1.0, "-1.0 dBTP")]
    [InlineData(double.NegativeInfinity, "-∞ dBTP")]
    public void DbtpLabel_formats(double dbtp, string expected) => Assert.Equal(expected, MixerFormat.DbtpLabel(dbtp));

    [Theory]
    [InlineData(0.0, 1.0)]                        // at ceiling → full
    [InlineData(-60.0, 0.0)]                      // at floor → empty
    [InlineData(-30.0, 0.5)]                      // half-way (default -60..0)
    [InlineData(double.NegativeInfinity, 0.0)]    // silence → empty
    [InlineData(6.0, 1.0)]                        // above ceiling clamps
    public void MeterFillFraction_maps_db_to_zero_one(double db, double expected) =>
        Assert.Equal(expected, MixerFormat.MeterFillFraction(db), 6);
}
