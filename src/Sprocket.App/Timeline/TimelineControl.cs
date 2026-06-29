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
    private const double DefaultHeaderWidth = 132;
    private const double MinHeaderWidth = 72;
    private const double MaxHeaderWidth = 360;
    private const double RulerHeight = 26;
    private const double TrackHeight = 46;
    private const double TrackGap = 4;
    private const double EdgeGrip = 7;
    private const double NameLeft = 10;
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

    // Width of the left track-header column. Resizable by dragging its right edge (session-only).
    private double _headerWidth = DefaultHeaderWidth;
    private bool _resizingHeader;

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

    // Linked companions captured at drag start (their track, clip, and original start) plus the group's
    // minimum original start, so a linked move shifts every member by one locked delta and none goes negative.
    private List<(Clip clip, Timecode origStart)> _dragLinked = [];
    private long _dragGroupMinStart;

    // Hand-tool panning state.
    private bool _panning;
    private double _panPressX, _panOrigScroll;

    // Drag-and-drop preview: the X of the drop indicator while a bin tile / effect hovers (PLAN.md step 16b).
    private double? _dropPreviewX;

    /// <summary>Raised when the selected clip changes (for the Inspector / header). Null = nothing selected.</summary>
    public event Action<Clip?>? SelectedClipChanged;

    /// <summary>
    /// Raised when a track name is double-clicked, requesting an inline rename. The <see cref="Rect"/> is the
    /// name area in control-local coordinates so the shell can position an editor over it (the timeline is
    /// custom-drawn and cannot host a child <c>TextBox</c> itself).
    /// </summary>
    public event Action<Track, Rect>? TrackRenameRequested;

    /// <summary>Whether edge/playhead snapping is active during drags.</summary>
    public bool Snapping { get; set; } = true;

    /// <summary>Whether linked A/V move and blade together (UI.md §3.2, PLAN.md step 13).</summary>
    public bool Linked { get; set; } = true;

    /// <summary>The active timeline tool (Select / Blade / Slip / Hand / Zoom).</summary>
    public EditTool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            Cursor = ToolCursor(value);
        }
    }

    private EditTool _activeTool = EditTool.Select;

    /// <summary>The cursor for a tool — shared by the <see cref="ActiveTool"/> setter and idle-hover restore.</summary>
    private static Cursor ToolCursor(EditTool tool) => tool switch
    {
        EditTool.Blade => new Cursor(StandardCursorType.Cross),
        EditTool.Hand => new Cursor(StandardCursorType.SizeAll),
        EditTool.Zoom => new Cursor(StandardCursorType.Hand),
        EditTool.Slip => new Cursor(StandardCursorType.SizeWestEast),
        _ => Cursor.Default,
    };

    /// <summary>The currently selected clip, or null.</summary>
    public Clip? SelectedClip => _selected;

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;

        // Drop target for media-bin tiles (place a clip) and Effects-browser rows (append an effect), PLAN.md
        // step 16b. The browser sets the payload under a DragFormats key; we route by which format is present.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropPreview());
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

    /// <summary>
    /// Zooms so the whole sequence fits the viewport width and scrolls back to the start (View ▸ Zoom to Fit,
    /// Shift+Z) — the Resolve/FCP "frame the timeline" command. No-op on an empty timeline or before layout.
    /// </summary>
    public void ZoomToFit()
    {
        if (_project is null)
            return;
        long durTicks = _project.Timeline.Duration.Ticks;
        double view = Bounds.Width - _headerWidth - 24; // small right inset so the tail isn't flush to the edge
        if (durTicks <= 0 || view <= 0)
            return;
        double seconds = (double)durTicks / Timecode.TicksPerSecond;
        _pxPerSecond = Math.Clamp(view / seconds, MinPxPerSecond, MaxPxPerSecond);
        _scrollX = 0;
        ClampScroll();
        InvalidateVisual();
    }

    private double AnchorX() => TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, _headerWidth);

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

    // ── Clip editing (Edit / Clip menus, PLAN.md step 16c) ──────────────────────────────────────────

    // A single-clip clipboard for cut/copy/paste; the flag records whether it was copied from a video lane so a
    // paste lands on a track of the matching kind.
    private (Clip clip, bool isVideo)? _clipboard;

    /// <summary>Whether a clip is selected (drives Edit ▸ Cut/Copy/Delete and Clip ▸ Nudge enablement).</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>Whether there is a clip on the clipboard to paste (drives Edit ▸ Paste enablement).</summary>
    public bool CanPaste => _clipboard is not null;

    /// <summary>Whether the selected clip is part of a linked A/V group (drives Clip ▸ Unlink enablement).</summary>
    public bool SelectedIsLinked => _selected?.LinkGroupId is not null;

    /// <summary>Copies the selected clip to the clipboard (a detached deep copy, link cleared — steps 13/16c).</summary>
    public void CopySelected()
    {
        if (_selected is null)
            return;
        _clipboard = (ClipboardOps.Copy(_selected), TrackOf(_selected) is VideoTrack);
    }

    /// <summary>Copies then deletes the selected clip (and its linked companions when Linked is on).</summary>
    public void CutSelected()
    {
        if (_selected is null)
            return;
        CopySelected();
        DeleteSelected();
    }

    /// <summary>
    /// Pastes the clipboard clip at the playhead, onto the first track of the matching kind. The pasted clip is
    /// independent (no link) and becomes the selection — one undoable <see cref="AddClipCommand"/> (step 10).
    /// </summary>
    public void PasteAtPlayhead()
    {
        if (_clipboard is not { } cb || _history is null || _project is null)
            return;
        Track? target = cb.isVideo
            ? (Track?)_project.Timeline.VideoTracks.FirstOrDefault()
            : _project.Timeline.AudioTracks.FirstOrDefault();
        if (target is null)
            return;

        Clip pasted = ClipboardOps.Paste(cb.clip, _playhead);
        Execute(new AddClipCommand(target, pasted));
        Select(pasted);
        ClipPlaced?.Invoke();
    }

    /// <summary>
    /// Deletes the selected clip (and, when Linked is on, its companion A/V clips) as one undo entry, then clears
    /// the selection.
    /// </summary>
    public void DeleteSelected()
    {
        if (_selected is null || _history is null || _project is null)
            return;
        Track? track = TrackOf(_selected);
        if (track is null)
            return;

        var removals = new List<IEditCommand> { new RemoveClipCommand(track, _selected) };
        if (Linked)
            foreach ((Track ctrack, Clip cclip) in _project.Timeline.ClipsLinkedTo(_selected))
                removals.Add(new RemoveClipCommand(ctrack, cclip));

        Execute(removals.Count == 1 ? removals[0] : new CompositeCommand("Delete clips", removals));
        Select(null);
    }

    /// <summary>
    /// Nudges the selected clip by <paramref name="frames"/> frames along the timeline (Clip ▸ Nudge Left/Right).
    /// With Linked on the whole group shifts together; the move is clamped so no member crosses the origin. Each
    /// press is its own undo entry.
    /// </summary>
    public void NudgeSelected(int frames)
    {
        if (_selected is null || _history is null || _project is null || frames == 0)
            return;
        long frameTicks = FrameTicks();
        if (frameTicks <= 0)
            return;

        List<Clip> linked = (Linked ? _project.Timeline.ClipsLinkedTo(_selected).Select(l => l.Clip) : [])
            .ToList();
        long groupMin = _selected.TimelineStart.Ticks;
        foreach (Clip c in linked)
            groupMin = Math.Min(groupMin, c.TimelineStart.Ticks);

        long delta = ClipboardOps.ClampGroupNudge((long)frames * frameTicks, groupMin);
        if (delta == 0)
            return;

        if (linked.Count > 0)
        {
            var commands = new List<IEditCommand> { Shift(_selected, delta) };
            foreach (Clip c in linked)
                commands.Add(Shift(c, delta));
            Execute(new CompositeCommand("Nudge clips", commands));
        }
        else
        {
            Execute(Shift(_selected, delta));
        }

        static SetClipPlacementCommand Shift(Clip c, long delta) =>
            new(c, c.SourceIn, c.SourceOut, new Timecode(c.TimelineStart.Ticks + delta), "Nudge clip");
    }

    /// <summary>Unlinks the selected clip and its companions (clears their link group) as one undo entry (step 13).</summary>
    public void UnlinkSelected()
    {
        if (_selected is null || _selected.LinkGroupId is null || _history is null || _project is null)
            return;
        var members = new List<Clip> { _selected };
        members.AddRange(_project.Timeline.ClipsLinkedTo(_selected).Select(l => l.Clip));

        var commands = members
            .Select(c => (IEditCommand)SetPropertyCommand<Guid?>.Create(
                "Unlink", () => c.LinkGroupId, v => c.LinkGroupId = v, null))
            .ToList();
        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Unlink clips", commands));
    }

    /// <summary>Appends an effect (by catalog id) to the selected clip via <see cref="AddEffectCommand"/> (steps 15–16).</summary>
    public void ApplyEffectToSelected(string effectTypeId)
    {
        if (_selected is null || _history is null || string.IsNullOrEmpty(effectTypeId))
            return;
        EffectInstance instance = EffectCatalog.Find(effectTypeId)?.CreateInstance() ?? new EffectInstance(effectTypeId);
        Execute(new AddEffectCommand(_selected, instance));
    }

    /// <summary>Inserts a generator clip (title, colour matte) at the playhead (PLAN.md step 19), selecting it.</summary>
    public void InsertGenerator(GeneratorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        InsertSyntheticVideoClip(
            t => descriptor.CreateClip(GeneratorCatalog.DefaultDuration, t), $"Insert {descriptor.DisplayName}");
    }

    /// <summary>Inserts an adjustment layer at the playhead (PLAN.md step 19): its effects grade the tracks below
    /// it for its span. Always lands on a track above the content so it doesn't displace it.</summary>
    public void InsertAdjustmentLayer() =>
        // Fully qualified: a Control already has a `Clip` property (Geometry), which would shadow the model type here.
        InsertSyntheticVideoClip(t => Sprocket.Core.Model.Clip.CreateAdjustment(GeneratorCatalog.DefaultDuration, t), "Insert adjustment layer");

    /// <summary>
    /// Adds a synthetic (generator / adjustment) clip at the playhead. It lands on the topmost video track when that
    /// track is free at the playhead; otherwise a new video track is created above so the clip stacks over (not
    /// displaces) existing content — both as one undoable entry. The new clip becomes the selection.
    /// </summary>
    private void InsertSyntheticVideoClip(Func<Timecode, Clip> create, string label)
    {
        if (_history is null || _project is null)
            return;

        Clip clip = create(_playhead);
        VideoTrack? top = _project.Timeline.VideoTracks.LastOrDefault();

        if (top is not null && top.ResolveActiveClip(_playhead) is null && top.ResolveActiveClip(clip.TimelineEnd - new Timecode(1)) is null)
        {
            Execute(new AddClipCommand(top, clip));
        }
        else
        {
            // Stack on a fresh top track so an adjustment grades the tracks beneath and a generator overlays them.
            var track = new VideoTrack { Name = $"V{_project.Timeline.VideoTracks.Count() + 1}" };
            Execute(new CompositeCommand(label,
            [
                new AddTrackCommand(_project.Timeline, track),
                new AddClipCommand(track, clip),
            ]));
        }

        Select(clip);
        ClipPlaced?.Invoke();
    }

    private long FrameTicks()
    {
        Rational fps = _project!.Timeline.FrameRate;
        return fps.Num > 0 ? Timecode.FromFrames(1, fps).Ticks : 0;
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
        DrawDropPreview(ctx, size);
    }

    // A dashed accent line where a dragged bin tile would place a clip (PLAN.md step 16b).
    private void DrawDropPreview(DrawingContext ctx, Size size)
    {
        if (_dropPreviewX is not { } x || x < _headerWidth || x > size.Width)
            return;
        var pen = new Pen(Accent, 1.5) { DashStyle = new DashStyle([3, 3], 0) };
        ctx.DrawLine(pen, new Point(x, RulerHeight), new Point(x, size.Height));
    }

    private void DrawRuler(DrawingContext ctx, Size size)
    {
        ctx.FillRectangle(RulerBg, new Rect(0, 0, size.Width, RulerHeight));
        ctx.DrawLine(EdgePen, new Point(0, RulerHeight), new Point(size.Width, RulerHeight));

        long interval = TimelineMath.RulerIntervalTicks(_pxPerSecond, 90);
        long firstTicks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(_headerWidth, _pxPerSecond, _scrollX, _headerWidth));
        long t = firstTicks - (firstTicks % interval);
        using (ctx.PushClip(new Rect(_headerWidth, 0, size.Width - _headerWidth, RulerHeight)))
        {
            for (; ; t += interval)
            {
                double x = TimelineMath.XAtTicks(t, _pxPerSecond, _scrollX, _headerWidth);
                if (x > size.Width)
                    break;
                if (x < _headerWidth - 1)
                    continue;
                ctx.DrawLine(GridPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
                ctx.DrawText(Label(TimeLabel(t), 10.5, MutedText), new Point(x + 4, 5));
            }
        }
    }

    private void DrawClips(DrawingContext ctx, Size size, List<(Track track, bool isVideo)> lanes)
    {
        using var _ = ctx.PushClip(new Rect(_headerWidth, RulerHeight, size.Width - _headerWidth, size.Height - RulerHeight));
        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i) + 3;
            double h = TrackHeight - 6;

            foreach (Clip clip in track.Clips)
            {
                double x0 = TimelineMath.XAtTicks(clip.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                double x1 = TimelineMath.XAtTicks(clip.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                if (x1 < _headerWidth || x0 > size.Width)
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
        ctx.FillRectangle(HeaderBg, new Rect(0, 0, _headerWidth, Bounds.Height));
        ctx.DrawLine(EdgePen, new Point(_headerWidth, 0), new Point(_headerWidth, Bounds.Height));

        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i);
            // Clip the name to the area left of the toggles so a long name can't bleed over them or past
            // the (now resizable) column edge.
            using (ctx.PushClip(new Rect(NameLeft, top, NameAreaWidth(isVideo), TrackHeight)))
                ctx.DrawText(Label(TrackName(track, isVideo), 11.5, Text), new Point(NameLeft, top + 7));

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
        // Center the glyph in the box rather than a fixed left offset — narrower glyphs (S, the eye)
        // otherwise sit left with extra space on their right.
        var text = Label(glyph, 10, on ? Brushes.White : MutedText);
        ctx.DrawText(text, new Point(
            box.X + (box.Width - text.Width) / 2,
            box.Y + (box.Height - text.Height) / 2));
    }

    private Rect MuteBox(double laneTop) => new(_headerWidth - 56, laneTop + TrackHeight - 24, 22, 17);
    private Rect SoloBox(double laneTop) => new(_headerWidth - 30, laneTop + TrackHeight - 24, 22, 17);
    private Rect EnableBox(double laneTop) => new(_headerWidth - 30, laneTop + TrackHeight - 24, 22, 17);

    // Width available for the track-name text on a lane: from NameLeft to just left of that kind's toggles.
    private double NameAreaWidth(bool isVideo) =>
        Math.Max(0, (isVideo ? _headerWidth - 30 : _headerWidth - 56) - 6 - NameLeft);

    private void DrawPlayhead(DrawingContext ctx, Size size)
    {
        double x = TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, _headerWidth);
        if (x < _headerWidth || x > size.Width)
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

        // Drag the column's right edge to resize the header (checked before the header branch, since the
        // grip band straddles the boundary).
        if (p.Y > RulerHeight && Math.Abs(p.X - _headerWidth) <= EdgeGrip)
        {
            _resizingHeader = true;
            e.Pointer.Capture(this);
            return;
        }

        // Track-header column: double-click a name to rename; single-click hits the toggles.
        if (p.X < _headerWidth)
        {
            if (e.ClickCount == 2)
                TryBeginRename(p);
            else
                HandleHeaderClick(p);
            return;
        }

        // View tools act anywhere in the lane/ruler area.
        if (_activeTool == EditTool.Zoom && p.X >= _headerWidth)
        {
            bool zoomOut = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
            SetZoom(_pxPerSecond * (zoomOut ? 0.8 : 1.25), p.X);
            return;
        }
        if (_activeTool == EditTool.Hand && p.X >= _headerWidth)
        {
            _panning = true;
            _panPressX = p.X;
            _panOrigScroll = _scrollX;
            e.Pointer.Capture(this);
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

        // Clip body / edges.
        if (TryHitClip(p, out Clip? clip, out ClipDragMode mode) && clip is not null)
        {
            if (_activeTool == EditTool.Blade)
            {
                Select(clip);
                BladeClip(clip, p);
                return;
            }

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

        if (_resizingHeader)
        {
            _headerWidth = Math.Clamp(p.X, MinHeaderWidth, MaxHeaderWidth);
            ClampScroll();
            InvalidateVisual();
            return;
        }
        if (_scrubbing)
        {
            SeekToX(p.X);
            return;
        }
        if (_panning)
        {
            _scrollX = Math.Max(0, _panOrigScroll - (p.X - _panPressX));
            ClampScroll();
            InvalidateVisual();
            return;
        }
        if (_dragClip is not null)
        {
            UpdateClipDrag(p);
            return;
        }

        // Idle hover: show a resize cursor over the column edge, and the full track name as a tooltip when
        // the name is too long to fit the current column width.
        bool overGrip = p.Y > RulerHeight && Math.Abs(p.X - _headerWidth) <= EdgeGrip;
        Cursor = overGrip ? new Cursor(StandardCursorType.SizeWestEast) : ToolCursor(_activeTool);
        UpdateHeaderTooltip(p, overGrip);
    }

    // Sets the control tooltip to the full track name while hovering a truncated name in the header column;
    // clears it otherwise so no redundant tooltip shows for names that already fit.
    private void UpdateHeaderTooltip(Point p, bool overGrip)
    {
        string? tip = null;
        if (!overGrip && p.X < _headerWidth && p.Y > RulerHeight)
        {
            List<(Track track, bool isVideo)> lanes = Lanes();
            int i = LaneAtY(p.Y);
            if (i >= 0 && i < lanes.Count)
            {
                (Track track, bool isVideo) = lanes[i];
                string name = TrackName(track, isVideo);
                if (Label(name, 11.5, Text).Width > NameAreaWidth(isVideo))
                    tip = name;
            }
        }

        if (!Equals(ToolTip.GetTip(this), tip))
            ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _scrubbing = false;
        _panning = false;
        _resizingHeader = false;
        if (_dragClip is not null)
        {
            _coalesce?.Dispose(); // seal the gesture as one undo entry
            _coalesce = null;
            _dragClip = null;
            _dragMode = ClipDragMode.None;
            _dragLinked = [];
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

    // A double-click on a track name (not on its toggle buttons) requests an inline rename: raises
    // TrackRenameRequested with the name area's rect so the shell can position an editor over it.
    private void TryBeginRename(Point p)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return;
        (Track track, bool isVideo) = lanes[i];
        double top = LaneTop(i);

        // Ignore double-clicks that land on the toggles — they keep their single-click behaviour.
        if (EnableBox(top).Contains(p) || MuteBox(top).Contains(p) || SoloBox(top).Contains(p))
            return;

        var rect = new Rect(NameLeft - 2, top + 4, NameAreaWidth(isVideo) + 2, 20);
        TrackRenameRequested?.Invoke(track, rect);
    }

    /// <summary>
    /// Commits an inline track rename through the edit history (one undoable <see cref="SetPropertyCommand{T}"/>),
    /// mirroring the track toggles. No-op when the trimmed name is unchanged. Called by the shell's editor.
    /// </summary>
    public void CommitTrackRename(Track track, string newName)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_history is null)
            return;
        string trimmed = (newName ?? string.Empty).Trim();
        if (trimmed == track.Name)
            return;
        Execute(SetPropertyCommand<string>.Create(
            "Rename track", () => track.Name, v => track.Name = v, trimmed));
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
            double x0 = TimelineMath.XAtTicks(c.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            double x1 = TimelineMath.XAtTicks(c.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
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
        _dragPressTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        _dragOrigIn = clip.SourceIn;
        _dragOrigOut = clip.SourceOut;
        _dragOrigStart = clip.TimelineStart;
        _snapPoints = BuildSnapPoints(clip);

        // Capture linked companions for a linked move so the whole group shifts by one locked delta.
        _dragLinked = (Linked ? _project!.Timeline.ClipsLinkedTo(clip) : [])
            .Select(l => (l.Clip, l.Clip.TimelineStart)).ToList();
        _dragGroupMinStart = _dragOrigStart.Ticks;
        foreach ((Clip _, Timecode origStart) in _dragLinked)
            _dragGroupMinStart = Math.Min(_dragGroupMinStart, origStart.Ticks);

        _coalesce = _history!.BeginCoalescing();
    }

    private void UpdateClipDrag(Point p)
    {
        long pointerTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        long delta = pointerTicks - _dragPressTicks;

        // Slip tool: shift the source window, keep the clip's timeline position and duration fixed.
        if (_activeTool == EditTool.Slip)
        {
            UpdateSlip(delta);
            return;
        }

        long newIn = _dragOrigIn.Ticks, newOut = _dragOrigOut.Ticks, newStart = _dragOrigStart.Ticks;
        long dur = _dragOrigOut.Ticks - _dragOrigIn.Ticks;

        switch (_dragMode)
        {
            case ClipDragMode.Move:
                // Clamp the delta so no group member would cross t=0, then snap the primary's start.
                delta = Math.Max(delta, -_dragGroupMinStart);
                newStart = SnapMove(_dragOrigStart.Ticks + delta, dur);
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

        // A linked move shifts every companion by the same actual delta (their source spans unchanged), as one
        // undo entry. Trims stay per-clip (NLE convention) — only Move propagates across the link group.
        if (_dragMode == ClipDragMode.Move && _dragLinked.Count > 0)
        {
            long actualDelta = newStart - _dragOrigStart.Ticks;
            var commands = new List<IEditCommand>
            {
                new SetClipPlacementCommand(_dragClip!, new Timecode(newIn), new Timecode(newOut), new Timecode(newStart), "Move clip"),
            };
            foreach ((Clip companion, Timecode origStart) in _dragLinked)
                commands.Add(new SetClipPlacementCommand(
                    companion, companion.SourceIn, companion.SourceOut, new Timecode(origStart.Ticks + actualDelta), "Move clip"));
            Execute(new CompositeCommand("Move linked clips", commands));
            return;
        }

        string label = _dragMode == ClipDragMode.Move ? "Move clip" : "Trim clip";
        Execute(new SetClipPlacementCommand(
            _dragClip!, new Timecode(newIn), new Timecode(newOut), new Timecode(newStart), label));
    }

    /// <summary>
    /// Slips the dragged clip's source window by <paramref name="rawDelta"/> ticks, clamped to the media so
    /// the visible content shifts but the clip neither moves nor changes duration (PLAN.md step 13). Dragging
    /// right reveals later source content. Coalesces into one undo entry like the other drag gestures.
    /// </summary>
    private void UpdateSlip(long rawDelta)
    {
        long mediaDuration = MediaDurationTicks(_dragClip!);
        long slip = TimelineMath.ClampSlip(_dragOrigIn.Ticks, _dragOrigOut.Ticks, mediaDuration, rawDelta);
        Execute(new SetClipPlacementCommand(
            _dragClip!,
            new Timecode(_dragOrigIn.Ticks + slip),
            new Timecode(_dragOrigOut.Ticks + slip),
            _dragOrigStart,
            "Slip clip"));
    }

    /// <summary>
    /// Blade (razor) split at the cursor: splits the clip under the pointer at <paramref name="p"/>'s timeline
    /// time (snapped to the playhead when snapping is on). With Linked on, every companion clip that also spans
    /// the cut is split too and the right-hand halves share a fresh link group — so each side stays an
    /// independently linked A/V pair. The whole cut is one undo entry. A cut on a clip's very edge is ignored.
    /// </summary>
    private void BladeClip(Clip clip, Point p)
    {
        Track? track = TrackOf(clip);
        if (track is null)
            return;

        long atTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        if (Snapping)
            atTicks = TimelineMath.Snap(atTicks, [_playhead.Ticks], SnapTolerancePx, _pxPerSecond);
        var at = new Timecode(atTicks);

        // Must fall strictly inside the clicked clip.
        if (at <= clip.TimelineStart || at >= clip.TimelineEnd)
            return;

        List<(Track Track, Clip Clip)> companions = Linked
            ? _project!.Timeline.ClipsLinkedTo(clip).Where(l => l.Clip.Contains(at)).ToList()
            : [];
        Guid? rightGroup = (Linked && clip.LinkGroupId is not null && companions.Count > 0) ? Guid.NewGuid() : null;

        var primary = new SplitClipCommand(track, clip, at, rightGroup);
        if (companions.Count == 0)
        {
            Execute(primary);
            Select(primary.RightClip);
            return;
        }

        var commands = new List<IEditCommand> { primary };
        foreach ((Track ctrack, Clip cclip) in companions)
            commands.Add(new SplitClipCommand(ctrack, cclip, at, rightGroup));
        Execute(new CompositeCommand("Blade linked clips", commands));
        Select(primary.RightClip);
    }

    private Track? TrackOf(Clip clip)
    {
        foreach (Track t in _project!.Timeline.Tracks)
            if (t.Clips.Contains(clip))
                return t;
        return null;
    }

    private long MediaDurationTicks(Clip clip)
    {
        MediaRef? media = _project?.MediaPool.Get(clip.MediaRefId);
        // When the source duration is unknown (offline media), fall back to the current out-point so slip is
        // a no-op rather than running off an unknown end.
        return media is { Info.Duration.Ticks: > 0 } ? media.Info.Duration.Ticks : clip.SourceOut.Ticks;
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

    // ── Drag-and-drop (media bin → lane, effect → clip) ─────────────────────────────────────────────

    /// <summary>Raised when a clip is placed by a media-bin drop, so the shell can refresh the timeline header.</summary>
    public event Action? ClipPlaced;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool media = e.DataTransfer.Contains(DragFormats.MediaRefId);
        bool effect = e.DataTransfer.Contains(DragFormats.EffectId);
        if (!media && !effect)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropPreview();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // Show the indicator at the snapped drop start (media) or just the cursor (effect lands on a clip).
        double x = e.GetPosition(this).X;
        _dropPreviewX = x < _headerWidth ? null : x;
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropPreview();
        if (_project is null || _history is null)
            return;

        Point p = e.GetPosition(this);
        if (p.X < _headerWidth)
            return;

        if (e.DataTransfer.Contains(DragFormats.MediaRefId))
            DropMedia(e.DataTransfer.TryGetValue(DragFormats.MediaRefId), p);
        else if (e.DataTransfer.Contains(DragFormats.EffectId))
            DropEffect(e.DataTransfer.TryGetValue(DragFormats.EffectId), p);
    }

    private void ClearDropPreview()
    {
        if (_dropPreviewX is null)
            return;
        _dropPreviewX = null;
        InvalidateVisual();
    }

    /// <summary>Places a clip for the dropped source on the lane under the cursor (with a linked companion on
    /// the first track of the other kind when the source has both A/V), via <see cref="ClipPlacement"/>.</summary>
    private void DropMedia(string? idText, Point p)
    {
        if (!Guid.TryParse(idText, out Guid guid))
            return;
        MediaRef? media = _project!.MediaPool.Get(new MediaRefId(guid));
        if (media is null)
            return;

        (Track? dropped, bool isVideoLane) = TrackAndKindAtY(p.Y);
        VideoTrack? videoTarget = dropped as VideoTrack ?? _project.Timeline.VideoTracks.FirstOrDefault();
        AudioTrack? audioTarget = dropped as AudioTrack ?? _project.Timeline.AudioTracks.FirstOrDefault();
        bool primaryIsVideo = dropped is VideoTrack || (dropped is null && media.Info.HasVideo);

        long dropTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        long durationTicks = media.Info.Duration.Ticks;
        long start = ClipPlacement.SnapStart(
            dropTicks, durationTicks, DropSnapPoints(), Snapping, SnapTolerancePx, _pxPerSecond);

        ClipPlacement.PlacementResult? result = ClipPlacement.BuildPlaceCommand(
            media, videoTarget, audioTarget, start, Linked, primaryIsVideo);
        if (result is null)
            return;

        Execute(result.Value.Command);
        Select(result.Value.PrimaryClip);
        ClipPlaced?.Invoke();
    }

    /// <summary>Appends the dropped effect to the clip under the cursor (PLAN.md step 16b).</summary>
    private void DropEffect(string? effectId, Point p)
    {
        if (string.IsNullOrEmpty(effectId))
            return;
        if (!TryHitClip(p, out Clip? clip, out _) || clip is null)
            return;

        EffectInstance instance = EffectCatalog.Find(effectId)?.CreateInstance() ?? new EffectInstance(effectId);
        Execute(new AddEffectCommand(clip, instance));
        Select(clip);
    }

    private (Track? track, bool isVideo) TrackAndKindAtY(double y)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(y);
        return i >= 0 && i < lanes.Count ? lanes[i] : (null, false);
    }

    // Snap candidates for a drop: every clip edge plus the playhead and the origin (no clip is being dragged).
    private IReadOnlyList<long> DropSnapPoints()
    {
        var points = new List<long> { 0, _playhead.Ticks };
        foreach (Track track in _project!.Timeline.Tracks)
            foreach (Clip c in track.Clips)
            {
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
        long ticks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(x, _pxPerSecond, _scrollX, _headerWidth));
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
        long anchorTicks = TimelineMath.TicksAtX(anchorX, _pxPerSecond, _scrollX, _headerWidth);
        _pxPerSecond = clamped;
        _scrollX = Math.Max(0, _headerWidth - anchorX + TimelineMath.WidthOfTicks(anchorTicks, _pxPerSecond));
        ClampScroll();
        InvalidateVisual();
    }

    private void ClampScroll()
    {
        if (_project is null)
            return;
        double content = TimelineMath.WidthOfTicks(_project.Timeline.Duration.Ticks, _pxPerSecond) + 200;
        double view = Math.Max(0, Bounds.Width - _headerWidth);
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, content - view));
    }

    private string ClipName(Clip clip)
    {
        switch (clip.Kind)
        {
            case ClipKind.Adjustment:
                return "Adjustment Layer";
            case ClipKind.Generator when clip.Generator is not null:
                // Prefer a title's text, else the generator's display name.
                string text = clip.Generator.GetString(GeneratorParamNames.Text);
                return string.IsNullOrEmpty(text) ? GeneratorCatalog.DisplayName(clip.Generator.GeneratorTypeId) : text;
        }
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
