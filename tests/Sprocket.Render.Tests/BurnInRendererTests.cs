using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Pure geometry tests for the export burn-in layout (<see cref="BurnInRenderer.ComputeTextTopLeft"/>, PLAN.md
/// step 29). The actual glyph drawing (<see cref="BurnInRenderer.Draw"/>) is canvas- and font-bound (so its exact
/// pixels vary by platform font) and rests on the real-encode export test plus manual verification; the nine-point
/// anchoring that positions each line is asserted here without a surface.
/// </summary>
public sealed class BurnInRendererTests
{
    private static readonly SKRect Frame = SKRect.Create(0, 0, 1000, 600);
    private const float TextW = 200f;
    private const float TextH = 40f;
    private const float Margin = 20f;

    [Theory]
    [InlineData(BurnInPosition.TopLeft)]
    [InlineData(BurnInPosition.MiddleLeft)]
    [InlineData(BurnInPosition.BottomLeft)]
    public void LeftColumn_IsInsetByTheMarginFromTheLeftEdge(BurnInPosition position)
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, position, TextW, TextH, Margin);
        Assert.Equal(Frame.Left + Margin, p.X, 3);
    }

    [Theory]
    [InlineData(BurnInPosition.TopRight)]
    [InlineData(BurnInPosition.MiddleRight)]
    [InlineData(BurnInPosition.BottomRight)]
    public void RightColumn_PlacesTheTextBoxAgainstTheRightMargin(BurnInPosition position)
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, position, TextW, TextH, Margin);
        Assert.Equal(Frame.Right - Margin - TextW, p.X, 3);
    }

    [Theory]
    [InlineData(BurnInPosition.TopCenter)]
    [InlineData(BurnInPosition.Center)]
    [InlineData(BurnInPosition.BottomCenter)]
    public void CenterColumn_CentresTheTextBoxHorizontally(BurnInPosition position)
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, position, TextW, TextH, Margin);
        Assert.Equal(Frame.MidX - TextW / 2f, p.X, 3);
        Assert.Equal(Frame.MidX, p.X + TextW / 2f, 3);
    }

    [Theory]
    [InlineData(BurnInPosition.TopLeft)]
    [InlineData(BurnInPosition.TopCenter)]
    [InlineData(BurnInPosition.TopRight)]
    public void TopRow_IsInsetByTheMarginFromTheTopEdge(BurnInPosition position)
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, position, TextW, TextH, Margin);
        Assert.Equal(Frame.Top + Margin, p.Y, 3);
    }

    [Theory]
    [InlineData(BurnInPosition.BottomLeft)]
    [InlineData(BurnInPosition.BottomCenter)]
    [InlineData(BurnInPosition.BottomRight)]
    public void BottomRow_PlacesTheTextBoxAgainstTheBottomMargin(BurnInPosition position)
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, position, TextW, TextH, Margin);
        Assert.Equal(Frame.Bottom - Margin - TextH, p.Y, 3);
    }

    [Fact]
    public void MiddleRow_And_Center_AreVerticallyCentred()
    {
        SKPoint p = BurnInRenderer.ComputeTextTopLeft(Frame, BurnInPosition.Center, TextW, TextH, Margin);
        Assert.Equal(Frame.MidY, p.Y + TextH / 2f, 3);
    }

    [Fact]
    public void Draw_IsANoOp_ForADegenerateFrame_OrEmptyList()
    {
        var info = new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        // No throw, nothing to assert beyond that these degrade cleanly (mirrors MonitorOverlay's guard tests).
        BurnInRenderer.Draw(surface.Canvas, SKRect.Create(0, 0, 0, 64), [(BurnInPosition.TopLeft, "x")]);
        BurnInRenderer.Draw(surface.Canvas, SKRect.Create(0, 0, 64, 64), []);
    }
}
