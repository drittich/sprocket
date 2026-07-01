using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Inspector;

/// <summary>
/// A per-parameter keyframe lane for the Inspector (PLAN.md step 16b/16d). Two visual modes:
/// <list type="bullet">
/// <item><b>Strip</b> (compact): keyframes on a time line — square = Hold, diamond = Linear, circle = eased,
/// hexagon = custom Bezier.</item>
/// <item><b>Graph</b> (the editable velocity graph, step 16d): a value-vs-time plot with the live curve, where a
/// custom-Bezier segment exposes draggable handles so the velocity can be shaped freely.</item>
/// </list>
/// Both modes support <b>multi-select</b> (click, Shift-click toggle, rubber-band) and move the whole selection
/// together; right-click deletes (the selection, or just the clicked keyframe). All keyframe semantics live in
/// the pure <see cref="AnimatableEditing"/> / <see cref="KeyframeLaneMath"/> / <see cref="KeyframeGraphMath"/>
/// helpers and every edit is handed back to the owner to run through the command stack, so editing stays
/// undoable by construction (step 10). The drawing + pointer interaction rest on those + manual verification.
/// </summary>
public sealed class KeyframeLane : Control
{
    private const double StripHeight = 22;
    private const double GraphHeight = 132;
    private const double Pad = 5;          // left/right inset so edge keyframes aren't clipped
    private const double VPad = 12;        // top/bottom inset in graph mode
    private const double HitTolerancePx = 7;
    private const double Diamond = 4.5;
    private const double HandleR = 3.5;

    private static readonly IBrush LaneBg = Brush("#1A1A22");
    private static readonly IBrush KeyFill = Brush("#9AA4B2");   // Linear keyframes (diamond)
    private static readonly IBrush KeyHold = Brush("#C9893F");   // held keyframes (square)
    private static readonly IBrush KeyEase = Brush("#4FB286");   // eased keyframes (circle)
    private static readonly IBrush KeyBezier = Brush("#5AA9E6"); // custom Bezier keyframes (hexagon)
    // Accent + frame edge come from the shared Palette (Palette.cs); the keyframe/handle hues above are
    // component-specific and stay local.
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush CurveBrush = Brush("#7E8796");
    private static readonly IBrush HandleBrush = Brush("#5AA9E6");
    private static readonly Pen PlayheadPen = new(Palette.AccentBrush, 1);
    private static readonly Pen Frame = new(Palette.EdgeBrush, 1);
    private static readonly Pen SelectPen = new(Palette.AccentBrush, 1.5);
    private static readonly Pen HandlePen = new(Brush("#3E6E96"), 1);
    private static readonly IBrush RubberFill = new ImmutableSolidColorBrush(Palette.Accent, 0.15);
    private static readonly Pen RubberPen = new(Palette.AccentBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };

    private AnimatableValue _value = AnimatableValue.Constant(0);
    private long _rangeStart, _rangeEnd, _playhead;
    private double _valueMin, _valueMax = 1;
    private bool _graphMode;

    private readonly HashSet<long> _selected = new(); // selected keyframe times (ticks)

    private enum Drag { None, Keyframes, Handle, Rubber }
    private Drag _drag;
    private long _grabTicks;                 // the grabbed keyframe's time, re-anchored as the move proceeds
    private long _handleKeyframeTicks;        // the keyframe owning the dragged handle
    private bool _handleOutgoing;             // true = outgoing (c1) handle, false = incoming (c2) handle
    private Point _rubberStart, _rubberEnd;

    /// <summary>Raised when a keyframe / handle drag begins, so the owner can open one coalescing scope.</summary>
    public event Action? DragStarted;

    /// <summary>Raised when a drag ends.</summary>
    public event Action? DragEnded;

    /// <summary>Raised with the new value for any edit (move / add / delete / interpolation / handle). The owner
    /// runs it through the command stack; <c>coalesce</c> is true mid-drag so the whole drag is one undo entry.</summary>
    public event Action<AnimatableValue, bool>? Edited;

    public KeyframeLane()
    {
        Height = StripHeight;
        Margin = new Thickness(0, 2, 0, 2);
        ClipToBounds = true;
        UpdateTip();
        DoubleTapped += OnDoubleTappedHandler;
    }

    /// <summary>Whether the lane shows the value graph (with draggable Bezier handles) or the compact strip.</summary>
    public bool GraphMode
    {
        get => _graphMode;
        set
        {
            if (_graphMode == value)
                return;
            _graphMode = value;
            Height = value ? GraphHeight : StripHeight;
            UpdateTip();
            InvalidateVisual();
        }
    }

    private void UpdateTip() => ToolTip.SetTip(this, _graphMode
        ? "Drag a keyframe to move it (Shift-click / rubber-band to multi-select) · drag a handle to shape the curve · double-click empty to add · double-click a keyframe to cycle interpolation · right-click to delete"
        : "Drag a keyframe to move it (Shift-click / rubber-band to multi-select) · double-click to add · double-click a keyframe to cycle interpolation · right-click to delete");

    /// <summary>Updates what the lane displays (called by the Inspector's value refresher); triggers a redraw.
    /// <paramref name="valueMin"/>/<paramref name="valueMax"/> bound the graph-mode value axis.</summary>
    public void Update(AnimatableValue value, long rangeStartTicks, long rangeEndTicks, long playheadTicks,
        double valueMin, double valueMax)
    {
        _value = value;
        _rangeStart = rangeStartTicks;
        _rangeEnd = rangeEndTicks;
        _playhead = playheadTicks;
        _valueMin = valueMin;
        _valueMax = valueMax > valueMin ? valueMax : valueMin + 1;
        PruneSelection();
        InvalidateVisual();
    }

    private double LaneWidth => Math.Max(1, Bounds.Width - 2 * Pad);
    private double GraphDrawable => Math.Max(1, Bounds.Height - 2 * VPad);

    // Pixel position of a keyframe value in graph mode (strip mode pins everything to the mid-line).
    private double KeyX(long ticks) => Pad + KeyframeLaneMath.XAt(ticks, _rangeStart, _rangeEnd, LaneWidth);
    private double ValueY(double value) =>
        _graphMode ? VPad + KeyframeGraphMath.YForValue(value, _valueMin, _valueMax, GraphDrawable) : Bounds.Height / 2;

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.DrawRectangle(LaneBg, Frame, new RoundedRect(rect, 3));

        // Playhead marker.
        double px = KeyX(_playhead);
        ctx.DrawLine(PlayheadPen, new Point(px, 2), new Point(px, Bounds.Height - 2));

        IReadOnlyList<Keyframe> keys = _value.Keyframes;

        if (_graphMode)
            DrawCurve(ctx);
        else
            DrawStripConnector(ctx, keys);

        // Bezier handles (graph mode only), behind the keyframe markers.
        if (_graphMode)
            DrawHandles(ctx, keys);

        foreach (Keyframe k in keys)
        {
            double x = KeyX(k.Time.Ticks);
            double y = ValueY(k.Value);
            DrawKeyframe(ctx, x, y, k.Interpolation);
            if (_selected.Contains(k.Time.Ticks))
                ctx.DrawEllipse(null, SelectPen, new Point(x, y), Diamond + 3, Diamond + 3);
        }

        if (_drag == Drag.Rubber)
        {
            var band = NormRect(_rubberStart, _rubberEnd);
            ctx.DrawRectangle(RubberFill, RubberPen, band);
        }
    }

    private void DrawStripConnector(DrawingContext ctx, IReadOnlyList<Keyframe> keys)
    {
        double midY = Bounds.Height / 2;
        var line = new Pen(KeyFill, 1);
        Point? prev = null;
        foreach (Keyframe k in keys)
        {
            var here = new Point(KeyX(k.Time.Ticks), midY);
            if (prev is { } p)
                ctx.DrawLine(line, p, here);
            prev = here;
        }
    }

    private void DrawCurve(DrawingContext ctx)
    {
        if (!_value.IsAnimated)
            return;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            bool started = false;
            double width = Bounds.Width;
            for (double sx = Pad; sx <= width - Pad; sx += 2)
            {
                long ticks = KeyframeLaneMath.TicksAt(sx - Pad, _rangeStart, _rangeEnd, LaneWidth);
                double v = _value.Evaluate(new Timecode(ticks));
                var pt = new Point(sx, ValueY(v));
                if (!started) { g.BeginFigure(pt, false); started = true; }
                else g.LineTo(pt);
            }
            if (started)
                g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(CurveBrush, 1.5), geo);
    }

    private void DrawHandles(DrawingContext ctx, IReadOnlyList<Keyframe> keys)
    {
        for (int i = 0; i < keys.Count - 1; i++)
        {
            Keyframe k0 = keys[i], k1 = keys[i + 1];
            if (k0.Interpolation != Interpolation.Bezier)
                continue;
            (Point c1, Point c2) = HandlePoints(k0, k1);
            ctx.DrawLine(HandlePen, new Point(KeyX(k0.Time.Ticks), ValueY(k0.Value)), c1);
            ctx.DrawLine(HandlePen, new Point(KeyX(k1.Time.Ticks), ValueY(k1.Value)), c2);
            ctx.DrawEllipse(HandleBrush, null, c1, HandleR, HandleR);
            ctx.DrawEllipse(HandleBrush, null, c2, HandleR, HandleR);
        }
    }

    // Pixel positions of a Bezier segment's two control handles.
    private (Point c1, Point c2) HandlePoints(Keyframe k0, Keyframe k1)
    {
        BezierHandle h1 = k0.EaseOut ?? BezierHandle.DefaultEaseOut;
        BezierHandle h2 = k1.EaseIn ?? BezierHandle.DefaultEaseIn;
        double x0 = KeyX(k0.Time.Ticks), x1 = KeyX(k1.Time.Ticks);
        var c1 = new Point(
            x0 + (h1.X * (x1 - x0)),
            ValueY(KeyframeGraphMath.ValueForProgress(h1.Y, k0.Value, k1.Value)));
        var c2 = new Point(
            x0 + (h2.X * (x1 - x0)),
            ValueY(KeyframeGraphMath.ValueForProgress(h2.Y, k0.Value, k1.Value)));
        return (c1, c2);
    }

    private static void DrawKeyframe(DrawingContext ctx, double cx, double cy, Interpolation interp)
    {
        switch (interp)
        {
            case Interpolation.Hold:
                ctx.DrawRectangle(KeyHold, null, new Rect(cx - Diamond, cy - Diamond, Diamond * 2, Diamond * 2));
                return;

            case Interpolation.EaseIn:
            case Interpolation.EaseOut:
            case Interpolation.EaseInOut:
                ctx.DrawEllipse(KeyEase, null, new Point(cx, cy), Diamond, Diamond);
                return;

            case Interpolation.Bezier:
                DrawHexagon(ctx, cx, cy);
                return;

            default:
                DrawDiamond(ctx, cx, cy);
                return;
        }
    }

    private static void DrawDiamond(DrawingContext ctx, double cx, double cy)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(new Point(cx, cy - Diamond), true);
            g.LineTo(new Point(cx + Diamond, cy));
            g.LineTo(new Point(cx, cy + Diamond));
            g.LineTo(new Point(cx - Diamond, cy));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(KeyFill, null, geo);
    }

    private static void DrawHexagon(DrawingContext ctx, double cx, double cy)
    {
        double r = Diamond + 0.5;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            for (int i = 0; i < 6; i++)
            {
                double a = Math.PI / 3 * i;
                var p = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                if (i == 0) g.BeginFigure(p, true);
                else g.LineTo(p);
            }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(KeyBezier, null, geo);
    }

    // ── Pointer interaction ─────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_value.IsAnimated)
            return;

        Point p = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        int index = KeyframeLaneMath.NearestKeyframeIndex(
            p.X - Pad, _value.Keyframes, _rangeStart, _rangeEnd, LaneWidth, HitTolerancePx);

        if (props.IsRightButtonPressed)
        {
            if (index >= 0)
            {
                DeleteKeyframeOrSelection(_value.Keyframes[index].Time.Ticks);
                e.Handled = true;
            }
            return;
        }

        if (!props.IsLeftButtonPressed)
            return;

        // Graph mode: a Bezier handle takes priority over the keyframe under the cursor.
        if (_graphMode && TryGrabHandle(p))
        {
            _drag = Drag.Handle;
            DragStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (index >= 0)
        {
            long ticks = _value.Keyframes[index].Time.Ticks;
            if (shift)
            {
                // Toggle membership; don't start a move on a Shift-click.
                if (!_selected.Remove(ticks))
                    _selected.Add(ticks);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (!_selected.Contains(ticks))
            {
                _selected.Clear();
                _selected.Add(ticks);
            }
            _drag = Drag.Keyframes;
            _grabTicks = ticks;
            DragStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Empty space → rubber-band select (Shift keeps the current selection as a base).
        if (!shift)
            _selected.Clear();
        _drag = Drag.Rubber;
        _rubberStart = _rubberEnd = p;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None)
            return;
        Point p = e.GetPosition(this);

        switch (_drag)
        {
            case Drag.Keyframes:
                MoveSelection(KeyframeLaneMath.TicksAt(p.X - Pad, _rangeStart, _rangeEnd, LaneWidth));
                break;
            case Drag.Handle:
                DragHandle(p);
                break;
            case Drag.Rubber:
                _rubberEnd = p;
                UpdateRubberSelection();
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(null);
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_drag is Drag.Keyframes or Drag.Handle)
            DragEnded?.Invoke();
        bool wasRubber = _drag == Drag.Rubber;
        _drag = Drag.None;
        pointer?.Capture(null);
        if (wasRubber)
            InvalidateVisual();
    }

    private void MoveSelection(long toTicks)
    {
        long delta = toTicks - _grabTicks;
        if (delta == 0)
            return;
        AnimatableValue next = AnimatableEditing.NudgeKeyframes(_value, _selected.ToArray(), delta);
        // The selected keyframes all shift by the same delta, so re-anchor the selection + grab point.
        var shifted = _selected.Select(t => t + delta).ToHashSet();
        _selected.Clear();
        foreach (long t in shifted)
            _selected.Add(t);
        _grabTicks += delta;
        Edited?.Invoke(next, true);
    }

    private bool TryGrabHandle(Point p)
    {
        IReadOnlyList<Keyframe> keys = _value.Keyframes;
        for (int i = 0; i < keys.Count - 1; i++)
        {
            Keyframe k0 = keys[i], k1 = keys[i + 1];
            if (k0.Interpolation != Interpolation.Bezier)
                continue;
            (Point c1, Point c2) = HandlePoints(k0, k1);
            if (Near(p, c1))
            {
                _handleKeyframeTicks = k0.Time.Ticks;
                _handleOutgoing = true;
                return true;
            }
            if (Near(p, c2))
            {
                _handleKeyframeTicks = k1.Time.Ticks;
                _handleOutgoing = false;
                return true;
            }
        }
        return false;
    }

    private void DragHandle(Point p)
    {
        IReadOnlyList<Keyframe> keys = _value.Keyframes;
        int i = IndexOfTicks(keys, _handleKeyframeTicks);
        if (i < 0)
            return;

        // The segment this handle shapes: outgoing handle → [i, i+1]; incoming handle → [i-1, i].
        int seg0 = _handleOutgoing ? i : i - 1;
        if (seg0 < 0 || seg0 + 1 >= keys.Count)
            return;
        Keyframe k0 = keys[seg0], k1 = keys[seg0 + 1];

        long t0 = k0.Time.Ticks, t1 = k1.Time.Ticks;
        long ptTicks = KeyframeLaneMath.TicksAt(p.X - Pad, _rangeStart, _rangeEnd, LaneWidth);
        double frac = t1 == t0 ? 0 : Math.Clamp((double)(ptTicks - t0) / (t1 - t0), 0, 1);

        double value = KeyframeGraphMath.ValueForY(p.Y - VPad, _valueMin, _valueMax, GraphDrawable);
        BezierHandle current = (_handleOutgoing ? k0.EaseOut : k1.EaseIn)
            ?? (_handleOutgoing ? BezierHandle.DefaultEaseOut : BezierHandle.DefaultEaseIn);
        double progress = KeyframeGraphMath.ProgressForValue(value, k0.Value, k1.Value, current.Y);

        var handle = new BezierHandle(frac, progress);
        AnimatableValue next = _handleOutgoing
            ? AnimatableEditing.SetOutgoingHandle(_value, k0.Time, handle)
            : AnimatableEditing.SetIncomingHandle(_value, k1.Time, handle);
        Edited?.Invoke(next, true);
    }

    private void UpdateRubberSelection()
    {
        Rect band = NormRect(_rubberStart, _rubberEnd);
        _selected.Clear();
        foreach (Keyframe k in _value.Keyframes)
        {
            double x = KeyX(k.Time.Ticks);
            double y = ValueY(k.Value);
            // Strip mode has no value axis, so select on the X extent only; graph mode uses the full box.
            bool inside = x >= band.X && x <= band.Right && (!_graphMode || (y >= band.Y && y <= band.Bottom));
            if (inside)
                _selected.Add(k.Time.Ticks);
        }
    }

    private void DeleteKeyframeOrSelection(long clickedTicks)
    {
        if (_selected.Count > 1 && _selected.Contains(clickedTicks))
        {
            // Remove the whole multi-selection, one entry at a time (the value collapses to constant if all go).
            AnimatableValue next = _value;
            foreach (long t in _selected.ToArray())
                next = AnimatableEditing.RemoveKeyframe(next, new Timecode(t));
            _selected.Clear();
            Edited?.Invoke(next, false);
        }
        else
        {
            _selected.Remove(clickedTicks);
            Edited?.Invoke(AnimatableEditing.RemoveKeyframe(_value, new Timecode(clickedTicks)), false);
        }
    }

    private void OnDoubleTappedHandler(object? sender, TappedEventArgs e)
    {
        if (!_value.IsAnimated)
            return;

        Point p = e.GetPosition(this);
        int index = KeyframeLaneMath.NearestKeyframeIndex(
            p.X - Pad, _value.Keyframes, _rangeStart, _rangeEnd, LaneWidth, HitTolerancePx);

        if (index >= 0)
        {
            // Cycle the keyframe's interpolation mode (Linear → Ease In → Ease Out → Ease In/Out → Bezier → Hold → …).
            Keyframe k = _value.Keyframes[index];
            Edited?.Invoke(AnimatableEditing.CycleInterpolation(_value, k.Time), false);
        }
        else
        {
            // Add a keyframe at the clicked time. In graph mode the clicked Y sets its value; otherwise it
            // carries the value the parameter currently has there.
            long ticks = KeyframeLaneMath.TicksAt(p.X - Pad, _rangeStart, _rangeEnd, LaneWidth);
            var t = new Timecode(ticks);
            double value = _graphMode
                ? KeyframeGraphMath.ValueForY(p.Y - VPad, _valueMin, _valueMax, GraphDrawable)
                : _value.Evaluate(t);
            Edited?.Invoke(AnimatableEditing.UpsertKeyframe(_value, t, value), false);
        }
        e.Handled = true;
    }

    private void PruneSelection() => _selected.RemoveWhere(t => !_value.Keyframes.Any(k => k.Time.Ticks == t));

    private static int IndexOfTicks(IReadOnlyList<Keyframe> keys, long ticks)
    {
        for (int i = 0; i < keys.Count; i++)
            if (keys[i].Time.Ticks == ticks)
                return i;
        return -1;
    }

    private static bool Near(Point a, Point b) =>
        Math.Abs(a.X - b.X) <= HitTolerancePx && Math.Abs(a.Y - b.Y) <= HitTolerancePx;

    private static Rect NormRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
