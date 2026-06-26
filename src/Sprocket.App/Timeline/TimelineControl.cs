using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// The custom-drawn timeline (PLAN.md step 12, UI.md §3.6): a ruler + playhead, one lane per track (video on
/// top, audio below) with clips drawn as schematic filmstrip/waveform blocks, drag-to-move and edge-trim, track
/// mute/solo/enable toggles, zoom and horizontal scroll. Geometry lives in <see cref="TimelineMath"/> (pure,
/// tested); every model mutation flows through the step-10 <see cref="EditHistory"/> — a drag is one coalesced
/// undo entry. Real decoded thumbnails / waveforms are step 15; the slice draws schematic fills.
/// </summary>
public sealed class TimelineControl : Control
{
    // Layout constants (px).
    private const double HeaderWidth = 132;
    private const double RulerHeight = 26;
    private const double TrackHeight = 54;
    private const double TrackGap = 4;
    private const double EdgeGrip = 7;
    private const double MinPxPerSecond = 8;
    private const double MaxPxPerSecond = 600;
    private const double SnapTolerancePx = 8;

    private static readonly IBrush PaneBg = Brush("#101016");
    private static readonly IBrush RulerBg = Brush("#16161C");
    private static readonly IBrush HeaderBg = Brush("#1A1A22");
    private static readonly IBrush LaneEven = Brush("#14141B");
    private static readonly IBrush LaneOdd = Brush("#171720");
    private static readonly IBrush VideoFill = Brush("#2F3A5C");
    private static readonly IBrush AudioFill = Brush("#2C4A39");
    private static readonly IBrush ClipDetail = Brush("#4A567E");
    private static readonly IBrush AudioDetail = Brush("#4F7A60");
    private static readonly IBrush Text = Brush("#C9D1DA");
    private static readonly IBrush MutedText = Brush("#8A93A2");
    private static readonly IBrush FaintText = Brush("#6A7180");
    private static readonly IBrush Accent = Brush("#6C5CE7");
    private static readonly IBrush ToggleOn = Brush("#6C5CE7");
    private static readonly IBrush ToggleOff = Brush("#2A2A33");
    private static readonly Pen GridPen = new(Brush("#24242E"), 1);
    private static readonly Pen EdgePen = new(Brush("#2A2A33"), 1);
    private static readonly Pen PlayheadPen = new(Brush("#6C5CE7"), 1.5);
    private static readonly Pen SelectPen = new(Brush("#6C5CE7"), 2);

    private Project? _project;
    private EditHistory? _history;
    private PlaybackEngine? _engine;

    private double _pxPerSecond = 70;
    private double _scrollX;
    private Timecode _playhead = Timecode.Zero;

    private Clip? _selected;
    private bool _scrubbing;

    // Active clip-drag gesture state.
    private Clip? _dragClip;
    private ClipDragMode _dragMode = ClipDragMode.None;
    private long _dragPressTicks;
    private Timecode _dragOrigIn, _dragOrigOut, _dragOrigStart;
    private long _minDurTicks = 1;
    private IReadOnlyList<long> _snapPoints = [];
    private IDisposable? _coalesce;

    /// <summary>Raised when the selected clip changes (for the Inspector / header). Null = nothing selected.</summary>
    public event Action<Clip?>? SelectedClipChanged;

    /// <summary>Whether edge/playhead snapping is active during drags.</summary>
    public bool Snapping { get; set; } = true;

    /// <summary>The currently selected clip, or null.</summary>
    public Clip? SelectedClip => _selected;

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Binds the timeline to a project, the shared edit history, and the playback engine. Call once.</summary>
    public void Attach(Project project, EditHistory history, PlaybackEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        _project = project;
        _history = history;
        _engine = engine;

        Rational fps = project.Timeline.FrameRate;
        _minDurTicks = Math.Max(1, fps.Num > 0 ? Timecode.FromFrames(1, fps).Ticks : 1);

        _history.Changed += OnHistoryChanged;
        if (_engine is not null)
            _engine.PositionChanged += OnEnginePosition;

        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_history is not null)
            _history.Changed -= OnHistoryChanged;
        if (_engine is not null)
            _engine.PositionChanged -= OnEnginePosition;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Zooms in (buttons / Ctrl+wheel), keeping the playhead roughly in view.</summary>
    public void ZoomIn() => SetZoom(_pxPerSecond * 1.25, AnchorX());

    /// <summary>Zooms out.</summary>
    public void ZoomOut() => SetZoom(_pxPerSecond * 0.8, AnchorX());

    private double AnchorX() => TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, HeaderWidth);

    private void OnHistoryChanged()
    {
        // A clip may have been removed by undo/redo; drop a stale selection.
        if (_selected is not null && _project is not null
            && !_project.Timeline.Tracks.Any(t => t.Clips.Contains(_selected)))
        {
            _selected = null;
            SelectedClipChanged?.Invoke(null);
        }
        InvalidateVisual();
    }

    private void OnEnginePosition(Timecode t)
    {
        _playhead = t;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    // ── Lane layout ─────────────────────────────────────────────────────────────────────────────────

    private List<(Track track, bool isVideo)> Lanes()
    {
        var lanes = new List<(Track, bool)>();
        if (_project is null)
            return lanes;
        // Video tracks top→bottom (highest z on top), then audio tracks.
        foreach (VideoTrack v in _project.Timeline.VideoTracks.Reverse())
            lanes.Add((v, true));
        foreach (AudioTrack a in _project.Timeline.AudioTracks)
            lanes.Add((a, false));
        return lanes;
    }

    private static double LaneTop(int index) => RulerHeight + index * (TrackHeight + TrackGap);

    private int LaneAtY(double y)
    {
        if (y < RulerHeight)
            return -1;
        int index = (int)((y - RulerHeight) / (TrackHeight + TrackGap));
        return index;
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(PaneBg, new Rect(size));

        if (_project is null)
            return;

        List<(Track track, bool isVideo)> lanes = Lanes();

        // Lane backgrounds + separators.
        for (int i = 0; i < lanes.Count; i++)
        {
            double top = LaneTop(i);
            var laneRect = new Rect(0, top, size.Width, TrackHeight);
            ctx.FillRectangle(i % 2 == 0 ? LaneEven : LaneOdd, laneRect);
            ctx.DrawLine(GridPen, new Point(0, top + TrackHeight), new Point(size.Width, top + TrackHeight));
        }

        DrawRuler(ctx, size);
        DrawClips(ctx, size, lanes);
        DrawHeaders(ctx, lanes);
        DrawPlayhead(ctx, size);
    }

    private void DrawRuler(DrawingContext ctx, Size size)
    {
        ctx.FillRectangle(RulerBg, new Rect(0, 0, size.Width, RulerHeight));
        ctx.DrawLine(EdgePen, new Point(0, RulerHeight), new Point(size.Width, RulerHeight));

        long interval = TimelineMath.RulerIntervalTicks(_pxPerSecond, 90);
        long firstTicks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(HeaderWidth, _pxPerSecond, _scrollX, HeaderWidth));
        long t = firstTicks - (firstTicks % interval);
        using (ctx.PushClip(new Rect(HeaderWidth, 0, size.Width - HeaderWidth, RulerHeight)))
        {
            for (; ; t += interval)
            {
                double x = TimelineMath.XAtTicks(t, _pxPerSecond, _scrollX, HeaderWidth);
                if (x > size.Width)
                    break;
                if (x < HeaderWidth - 1)
                    continue;
                ctx.DrawLine(GridPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
                ctx.DrawText(Label(TimeLabel(t), 10.5, MutedText), new Point(x + 4, 5));
            }
        }
    }

    private void DrawClips(DrawingContext ctx, Size size, List<(Track track, bool isVideo)> lanes)
    {
        using var _ = ctx.PushClip(new Rect(HeaderWidth, RulerHeight, size.Width - HeaderWidth, size.Height - RulerHeight));
        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i) + 3;
            double h = TrackHeight - 6;

            foreach (Clip clip in track.Clips)
            {
                double x0 = TimelineMath.XAtTicks(clip.TimelineStart.Ticks, _pxPerSecond, _scrollX, HeaderWidth);
                double x1 = TimelineMath.XAtTicks(clip.TimelineEnd.Ticks, _pxPerSecond, _scrollX, HeaderWidth);
                if (x1 < HeaderWidth || x0 > size.Width)
                    continue;

                var rect = new Rect(x0, top, Math.Max(2, x1 - x0), h);
                var rounded = new RoundedRect(rect, 4);
                ctx.DrawRectangle(isVideo ? VideoFill : AudioFill, null, rounded);

                using (ctx.PushClip(rect))
                {
                    if (isVideo)
                        DrawFilmstrip(ctx, rect);
                    else
                        DrawWaveform(ctx, rect);
                    ctx.DrawText(Label(ClipName(clip), 11, Text), new Point(rect.X + 6, rect.Y + 4));
                }

                if (ReferenceEquals(clip, _selected))
                    ctx.DrawRectangle(null, SelectPen, rounded);
            }
        }
    }

    // Schematic only (real poster frames are step 15): even vertical dividers like a filmstrip.
    private static void DrawFilmstrip(DrawingContext ctx, Rect rect)
    {
        for (double x = rect.X + 22; x < rect.Right - 2; x += 26)
            ctx.DrawLine(new Pen(ClipDetail, 1), new Point(x, rect.Y + 16), new Point(x, rect.Bottom - 3));
    }

    // Schematic only (real waveforms are step 15): a deterministic bar pattern around the centre line.
    private static void DrawWaveform(DrawingContext ctx, Rect rect)
    {
        double mid = rect.Y + rect.Height * 0.62;
        var pen = new Pen(AudioDetail, 1);
        for (double x = rect.X + 4; x < rect.Right - 2; x += 3)
        {
            double phase = (x - rect.X) * 0.20;
            double amp = (rect.Height * 0.30) * (0.35 + 0.65 * Math.Abs(Math.Sin(phase) * Math.Cos(phase * 0.37)));
            ctx.DrawLine(pen, new Point(x, mid - amp), new Point(x, mid + amp));
        }
    }

    private void DrawHeaders(DrawingContext ctx, List<(Track track, bool isVideo)> lanes)
    {
        ctx.FillRectangle(HeaderBg, new Rect(0, 0, HeaderWidth, Bounds.Height));
        ctx.DrawLine(EdgePen, new Point(HeaderWidth, 0), new Point(HeaderWidth, Bounds.Height));

        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i);
            ctx.DrawText(Label(TrackName(track, isVideo), 11.5, Text), new Point(10, top + 7));

            if (isVideo)
            {
                DrawToggle(ctx, EnableBox(top), "👁", track.Enabled);
            }
            else
            {
                var audio = (AudioTrack)track;
                DrawToggle(ctx, MuteBox(top), "M", audio.Muted);
                DrawToggle(ctx, SoloBox(top), "S", audio.Solo);
            }
        }
    }

    private static void DrawToggle(DrawingContext ctx, Rect box, string glyph, bool on)
    {
        ctx.DrawRectangle(on ? ToggleOn : ToggleOff, null, new RoundedRect(box, 3));
        ctx.DrawText(Label(glyph, 10, on ? Brushes.White : MutedText), new Point(box.X + 5, box.Y + 1));
    }

    private static Rect MuteBox(double laneTop) => new(HeaderWidth - 56, laneTop + TrackHeight - 24, 22, 17);
    private static Rect SoloBox(double laneTop) => new(HeaderWidth - 30, laneTop + TrackHeight - 24, 22, 17);
    private static Rect EnableBox(double laneTop) => new(HeaderWidth - 30, laneTop + TrackHeight - 24, 22, 17);

    private void DrawPlayhead(DrawingContext ctx, Size size)
    {
        double x = TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, HeaderWidth);
        if (x < HeaderWidth || x > size.Width)
            return;
        ctx.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, size.Height));
        // A small downward triangle handle at the top.
        var handle = new StreamGeometry();
        using (StreamGeometryContext g = handle.Open())
        {
            g.BeginFigure(new Point(x - 5, 0), true);
            g.LineTo(new Point(x + 5, 0));
            g.LineTo(new Point(x, 8));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Accent, null, handle);
    }

    // ── Pointer interaction ─────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_project is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Point p = e.GetPosition(this);
        Focus();

        // Track-header toggles.
        if (p.X < HeaderWidth)
        {
            HandleHeaderClick(p);
            return;
        }

        // Ruler → scrub.
        if (p.Y < RulerHeight)
        {
            _scrubbing = true;
            e.Pointer.Capture(this);
            SeekToX(p.X);
            return;
        }

        // Clip body / edges → select + begin drag.
        if (TryHitClip(p, out Clip? clip, out ClipDragMode mode) && clip is not null)
        {
            Select(clip);
            BeginClipDrag(clip, mode, p);
            e.Pointer.Capture(this);
            return;
        }

        // Empty lane area → move the playhead and clear selection.
        Select(null);
        _scrubbing = true;
        e.Pointer.Capture(this);
        SeekToX(p.X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);

        if (_scrubbing)
        {
            SeekToX(p.X);
            return;
        }
        if (_dragClip is not null)
            UpdateClipDrag(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _scrubbing = false;
        if (_dragClip is not null)
        {
            _coalesce?.Dispose(); // seal the gesture as one undo entry
            _coalesce = null;
            _dragClip = null;
            _dragMode = ClipDragMode.None;
        }
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double anchorX = e.GetPosition(this).X;
            SetZoom(_pxPerSecond * (e.Delta.Y > 0 ? 1.2 : 1 / 1.2), anchorX);
        }
        else
        {
            _scrollX = Math.Max(0, _scrollX - e.Delta.Y * 50);
            ClampScroll();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private void HandleHeaderClick(Point p)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return;
        (Track track, bool isVideo) = lanes[i];
        double top = LaneTop(i);

        if (isVideo)
        {
            if (EnableBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle track", () => track.Enabled, v => track.Enabled = v, !track.Enabled));
        }
        else
        {
            var audio = (AudioTrack)track;
            if (MuteBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle mute", () => audio.Muted, v => audio.Muted = v, !audio.Muted));
            else if (SoloBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle solo", () => audio.Solo, v => audio.Solo = v, !audio.Solo));
        }
    }

    private bool TryHitClip(Point p, out Clip? clip, out ClipDragMode mode)
    {
        clip = null;
        mode = ClipDragMode.None;
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return false;

        Track track = lanes[i].track;
        // Last clip wins so a clip drawn on top (later in the list) is hit first.
        foreach (Clip c in track.Clips)
        {
            double x0 = TimelineMath.XAtTicks(c.TimelineStart.Ticks, _pxPerSecond, _scrollX, HeaderWidth);
            double x1 = TimelineMath.XAtTicks(c.TimelineEnd.Ticks, _pxPerSecond, _scrollX, HeaderWidth);
            ClipDragMode m = TimelineMath.HitMode(p.X, x0, x1, EdgeGrip);
            if (m != ClipDragMode.None)
            {
                clip = c;
                mode = m;
            }
        }
        return clip is not null;
    }

    private void BeginClipDrag(Clip clip, ClipDragMode mode, Point p)
    {
        _dragClip = clip;
        _dragMode = mode;
        _dragPressTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, HeaderWidth);
        _dragOrigIn = clip.SourceIn;
        _dragOrigOut = clip.SourceOut;
        _dragOrigStart = clip.TimelineStart;
        _snapPoints = BuildSnapPoints(clip);
        _coalesce = _history!.BeginCoalescing();
    }

    private void UpdateClipDrag(Point p)
    {
        long pointerTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, HeaderWidth);
        long delta = pointerTicks - _dragPressTicks;

        long newIn = _dragOrigIn.Ticks, newOut = _dragOrigOut.Ticks, newStart = _dragOrigStart.Ticks;
        long dur = _dragOrigOut.Ticks - _dragOrigIn.Ticks;

        switch (_dragMode)
        {
            case ClipDragMode.Move:
                newStart = TimelineMath.ClampNonNegative(_dragOrigStart.Ticks + delta);
                newStart = SnapMove(newStart, dur);
                break;

            case ClipDragMode.TrimEnd:
                newOut = Math.Max(_dragOrigIn.Ticks + _minDurTicks, _dragOrigOut.Ticks + delta);
                if (Snapping)
                {
                    long end = _dragOrigStart.Ticks + (newOut - _dragOrigIn.Ticks);
                    long snapped = TimelineMath.Snap(end, _snapPoints, SnapTolerancePx, _pxPerSecond);
                    if (snapped != end)
                        newOut = Math.Max(_dragOrigIn.Ticks + _minDurTicks, _dragOrigIn.Ticks + (snapped - _dragOrigStart.Ticks));
                }
                break;

            case ClipDragMode.TrimStart:
                newStart = TimelineMath.ClampNonNegative(_dragOrigStart.Ticks + delta);
                if (Snapping)
                    newStart = TimelineMath.Snap(newStart, _snapPoints, SnapTolerancePx, _pxPerSecond);
                long deltaActual = newStart - _dragOrigStart.Ticks;
                newIn = _dragOrigIn.Ticks + deltaActual;
                if (newIn < 0) { newStart -= newIn; newIn = 0; }
                if (newIn > _dragOrigOut.Ticks - _minDurTicks)
                {
                    long over = newIn - (_dragOrigOut.Ticks - _minDurTicks);
                    newIn -= over;
                    newStart -= over;
                }
                newStart = TimelineMath.ClampNonNegative(newStart);
                break;
        }

        string label = _dragMode == ClipDragMode.Move ? "Move clip" : "Trim clip";
        Execute(new SetClipPlacementCommand(
            _dragClip!, new Timecode(newIn), new Timecode(newOut), new Timecode(newStart), label));
    }

    private long SnapMove(long newStart, long dur)
    {
        if (!Snapping)
            return newStart;
        long snapStart = TimelineMath.Snap(newStart, _snapPoints, SnapTolerancePx, _pxPerSecond);
        if (snapStart != newStart)
            return snapStart;
        long end = newStart + dur;
        long snapEnd = TimelineMath.Snap(end, _snapPoints, SnapTolerancePx, _pxPerSecond);
        return snapEnd != end ? TimelineMath.ClampNonNegative(snapEnd - dur) : newStart;
    }

    private IReadOnlyList<long> BuildSnapPoints(Clip dragged)
    {
        var points = new List<long> { 0, _playhead.Ticks };
        foreach (Track track in _project!.Timeline.Tracks)
            foreach (Clip c in track.Clips)
            {
                if (ReferenceEquals(c, dragged))
                    continue;
                points.Add(c.TimelineStart.Ticks);
                points.Add(c.TimelineEnd.Ticks);
            }
        return points;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private void Execute(IEditCommand command) => _history!.Execute(command); // re-render happens via Changed

    private void Select(Clip? clip)
    {
        if (ReferenceEquals(clip, _selected))
            return;
        _selected = clip;
        SelectedClipChanged?.Invoke(clip);
        InvalidateVisual();
    }

    private void SeekToX(double x)
    {
        long ticks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(x, _pxPerSecond, _scrollX, HeaderWidth));
        Timecode t = new(ticks);
        if (_engine is not null)
            _engine.SeekTo(t); // engine echoes PositionChanged → playhead + redraw
        else
        {
            _playhead = t;
            InvalidateVisual();
        }
    }

    private void SetZoom(double pxPerSecond, double anchorX)
    {
        double clamped = Math.Clamp(pxPerSecond, MinPxPerSecond, MaxPxPerSecond);
        if (Math.Abs(clamped - _pxPerSecond) < 1e-6)
            return;
        // Keep the tick under anchorX fixed across the zoom.
        long anchorTicks = TimelineMath.TicksAtX(anchorX, _pxPerSecond, _scrollX, HeaderWidth);
        _pxPerSecond = clamped;
        _scrollX = Math.Max(0, HeaderWidth - anchorX + TimelineMath.WidthOfTicks(anchorTicks, _pxPerSecond));
        ClampScroll();
        InvalidateVisual();
    }

    private void ClampScroll()
    {
        if (_project is null)
            return;
        double content = TimelineMath.WidthOfTicks(_project.Timeline.Duration.Ticks, _pxPerSecond) + 200;
        double view = Math.Max(0, Bounds.Width - HeaderWidth);
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, content - view));
    }

    private string ClipName(Clip clip)
    {
        MediaRef? media = _project?.MediaPool.Get(clip.MediaRefId);
        return media is null ? "clip" : System.IO.Path.GetFileName(media.AbsolutePath);
    }

    private static string TrackName(Track track, bool isVideo) =>
        string.IsNullOrEmpty(track.Name) ? (isVideo ? "V" : "A") : track.Name;

    private static string TimeLabel(long ticks)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, (double)ticks / Timecode.TicksPerSecond));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private static FormattedText Label(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
