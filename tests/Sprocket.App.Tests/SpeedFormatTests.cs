using Sprocket.App;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="SpeedFormat"/> (PLAN.md step 21): the percentage ↔ speed-ratio conversions the
/// Speed dialog and Inspector speed row share. The dialog/inspector UI rests on these + manual verification.
/// </summary>
public class SpeedFormatTests
{
    [Theory]
    [InlineData("100", 1, 1)]
    [InlineData("50", 1, 2)]
    [InlineData("200", 2, 1)]
    [InlineData("150", 3, 2)]
    [InlineData("25", 1, 4)]
    public void Parses_Percentage_To_Exact_Ratio(string text, int num, int den)
    {
        Assert.True(SpeedFormat.TryParsePercent(text, out Rational speed));
        Assert.Equal(new Rational(num, den), speed);
    }

    [Theory]
    [InlineData("0")]      // freeze (deferred)
    [InlineData("-50")]    // reverse (deferred)
    [InlineData("")]
    [InlineData("abc")]
    public void Rejects_Non_Positive_Or_Garbage(string text)
    {
        Assert.False(SpeedFormat.TryParsePercent(text, out Rational speed));
        Assert.Equal(Rational.One, speed); // left at unity
    }

    [Fact]
    public void Formats_Ratio_As_Percentage_Without_Trailing_Zeros()
    {
        Assert.Equal("100", SpeedFormat.ToPercentString(Rational.One));
        Assert.Equal("150", SpeedFormat.ToPercentString(new Rational(3, 2)));
        Assert.Equal("50", SpeedFormat.ToPercentString(new Rational(1, 2)));
    }

    [Fact]
    public void Percent_Round_Trips_Through_Parse()
    {
        var speed = new Rational(3, 2);
        Assert.True(SpeedFormat.TryParsePercent(SpeedFormat.ToPercentString(speed), out Rational back));
        Assert.Equal(speed, back);
    }
}
