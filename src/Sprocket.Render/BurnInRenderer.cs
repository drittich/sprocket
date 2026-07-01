using SkiaSharp;
using Sprocket.Core.Model;

namespace Sprocket.Render;

/// <summary>
/// Bakes export burn-in overlays — timecode / clip name / watermark text — onto the composited export frame
/// (PLAN.md step 29). Like <see cref="MonitorOverlay"/> this is a pure post-composite draw over the surface
/// canvas: it never touches decoded pixels (ARCHITECTURE.md §1) and runs only on the deterministic export render
/// (§5), never on the preview hot path. The <em>content</em> of each line (timecode format, which clip is on top)
/// is resolved upstream by <see cref="BurnInResolver"/> in Core; this layer only positions and draws the strings.
/// </summary>
/// <remarks>
/// The layout (<see cref="ComputeTextTopLeft"/>) is pure so it is unit-testable without a canvas. Each line is
/// drawn white over a translucent dark pill so it reads on any underlying content (the broadcast burn-in look),
/// with the anchor inset from the frame edge by <see cref="MarginFraction"/>.
/// </remarks>
public static class BurnInRenderer
{
    /// <summary>Default text height as a fraction of the frame height (~4.3% — a readable broadcast burn-in size).</summary>
    public const float DefaultHeightFraction = 0.043f;

    /// <summary>Edge inset for anchored burn-ins, as a fraction of the smaller frame dimension.</summary>
    public const float MarginFraction = 0.022f;

    /// <summary>
    /// The top-left point at which a <paramref name="textWidth"/>×<paramref name="textHeight"/> line should be
    /// drawn so it sits at <paramref name="position"/> inside <paramref name="frame"/>, inset by
    /// <paramref name="margin"/> on the anchored edges. Pure (no canvas) so it can be verified headlessly.
    /// </summary>
    public static SKPoint ComputeTextTopLeft(SKRect frame, BurnInPosition position, float textWidth, float textHeight, float margin)
    {
        float x = position switch
        {
            BurnInPosition.TopLeft or BurnInPosition.MiddleLeft or BurnInPosition.BottomLeft => frame.Left + margin,
            BurnInPosition.TopRight or BurnInPosition.MiddleRight or BurnInPosition.BottomRight => frame.Right - margin - textWidth,
            _ => frame.MidX - textWidth / 2f, // centre column
        };
        float y = position switch
        {
            BurnInPosition.TopLeft or BurnInPosition.TopCenter or BurnInPosition.TopRight => frame.Top + margin,
            BurnInPosition.BottomLeft or BurnInPosition.BottomCenter or BurnInPosition.BottomRight => frame.Bottom - margin - textHeight,
            _ => frame.MidY - textHeight / 2f, // middle row
        };
        return new SKPoint(x, y);
    }

    /// <summary>
    /// Draws the resolved burn-in <paramref name="lines"/> into <paramref name="frame"/>. Each entry pairs a
    /// <see cref="BurnInPosition"/> with the already-resolved text; empty strings are skipped. No-op for a
    /// degenerate frame or an empty/absent list.
    /// </summary>
    public static void Draw(
        SKCanvas canvas,
        SKRect frame,
        IReadOnlyList<(BurnInPosition Position, string Text)> lines,
        float heightFraction = DefaultHeightFraction)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (lines is null || lines.Count == 0 || frame.Width <= 0 || frame.Height <= 0)
            return;

        float textSize = Math.Max(1f, heightFraction * frame.Height);
        float margin = MarginFraction * Math.Min(frame.Width, frame.Height);

        using var font = new SKFont(SKTypeface.Default, textSize);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var backing = new SKPaint { Color = new SKColor(0, 0, 0, 0x99), IsAntialias = true };

        SKFontMetrics metrics = font.Metrics;
        float lineHeight = metrics.Descent - metrics.Ascent; // ascent is negative
        float padX = textSize * 0.30f;
        float padY = textSize * 0.16f;

        foreach ((BurnInPosition position, string content) in lines)
        {
            if (string.IsNullOrEmpty(content))
                continue;

            float textWidth = font.MeasureText(content);
            // The visible box is the glyph line height plus a little vertical padding on each side.
            float boxWidth = textWidth + padX * 2f;
            float boxHeight = lineHeight + padY * 2f;

            SKPoint topLeft = ComputeTextTopLeft(frame, position, boxWidth, boxHeight, margin);

            var box = new SKRect(topLeft.X, topLeft.Y, topLeft.X + boxWidth, topLeft.Y + boxHeight);
            float radius = boxHeight * 0.18f;
            canvas.DrawRoundRect(box, radius, radius, backing);

            // Baseline sits padY below the box top, offset by the font ascent.
            float baseline = topLeft.Y + padY - metrics.Ascent;
            canvas.DrawText(content, topLeft.X + padX, baseline, SKTextAlign.Left, font, text);
        }
    }
}
