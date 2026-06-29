using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sprocket.App.Inspector;
using Sprocket.App.MediaBrowser;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Persistence;
using Sprocket.Playback;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// The editor shell (PLAN.md step 11, UI.md §1/§2): a frameless window with custom chrome + inline menu bar,
/// splitter-resizable Project / Program / Inspector panes over a full-width Timeline, and a status bar with
/// live telemetry. The full menu / command surface is wired in step 16c — File (New / Open / Save / Save As /
/// Import / Export / Exit), Edit (undo/redo + clip cut/copy/paste/delete), Clip (unlink / nudge), Effects
/// (apply from the catalog), View (zoom / snapping / guides / panel toggles), Window (reset layout) and Help
/// (About) — every editing action routing through <see cref="EditHistory"/> so it stays undoable. Items whose
/// feature lands in a later step (Sequence, per-clip Enable/Speed, Select All, Link) stay visibly disabled.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlaybackEngine? _engine;
    private readonly Project? _project;
    private readonly EditHistory _history = new();

    private ThumbnailService? _thumbnails;
    private MediaBrowserPanel? _mediaBrowser;
    private InspectorPanel? _inspector;
    private TimelineControl? _timeline;
    private Clip? _selectedClip; // the timeline selection (keyframe navigation targets its keyframes, step 16d)

    // Dual monitors (PLAN.md step 17): the Program monitor wraps the main engine; the Source monitor previews
    // the selected clip's source. The transport bar drives whichever is active.
    private ProgramMonitor? _program;
    private SourceMonitor? _source;
    private IMonitor? _active;
    private PreviewSurface? _preview;
    private Button? _playPause;
    private Button? _prevKeyframeButton, _nextKeyframeButton;
    private Slider? _scrubber;
    private TextBlock? _positionText, _durationText;

    private bool _suppressSeek;        // guards programmatic scrubber updates from re-triggering a seek
    private bool _exporting;
    private int _savedUndoCount;       // history depth at the last save; document is clean while it matches
    private string _projectName = "Untitled";
    private string? _currentProjectPath; // the file this project was loaded from / last saved to (null = untitled)

    // Controls captured for later updates.
    private TextBlock? _statusText, _telemetryText, _engineStateText, _saveStateText, _timelineHeader;
    private MenuItem? _undoMenuItem, _redoMenuItem;
    private Button? _exportButton, _maxButton;
    private Control? _root;
    private WindowState _lastNonMinimizedState = WindowState.Normal; // persisted on close (ignores Minimized)

    // Command-menu items refreshed on submenu open (context-enabling) + the View toggles / panes.
    private MenuItem? _cutMenuItem, _copyMenuItem, _pasteMenuItem, _deleteMenuItem;
    private MenuItem? _unlinkMenuItem, _nudgeLeftMenuItem, _nudgeRightMenuItem;
    private MenuItem? _snappingMenuItem, _guidesMenuItem, _showProjectMenuItem, _showInspectorMenuItem;
    private System.Collections.Generic.IReadOnlyList<MenuItem> _effectsMenuItems = [];
    private ToggleButton? _snappingToggle, _guidesToggle;
    private Grid? _workspaceGrid, _outerGrid;
    private Border? _projectPane, _inspectorPane;
    private GridSplitter? _projectSplitter, _inspectorSplitter;

    /// <summary>The Sprocket project file type (a JSON sidecar) for the open / save-as pickers.</summary>
    private static readonly FilePickerFileType SprocketProjectFileType =
        new("Sprocket project") { Patterns = ["*.sprocket.json", "*.json"] };

    /// <summary>Raised when File ▸ New / Open wants the composition root to swap to a freshly built session over
    /// <see cref="SessionRequest.Project"/> (PLAN.md step 16c). Handled by <see cref="App"/>.</summary>
    public event Action<SessionRequest>? SessionRequested;

    /// <summary>A request to start a new editing session over an already-built project.</summary>
    /// <param name="Project">The new (empty) or freshly loaded project.</param>
    /// <param name="Status">A status line describing the session.</param>
    /// <param name="ProjectPath">The file it was loaded from, or <see langword="null"/> for an untitled project.</param>
    public readonly record struct SessionRequest(Project Project, string Status, string? ProjectPath);

    // Parameterless ctor for the XAML designer / tooling.
    public MainWindow() : this(null, null, string.Empty, null) { }

    public MainWindow(PlaybackEngine? engine, Project? project, string status, string? projectPath = null)
    {
        AvaloniaXamlLoader.Load(this);
        _engine = engine;
        _project = project;
        _currentProjectPath = projectPath;

        _root = this.FindControl<Control>("Root");
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _telemetryText = this.FindControl<TextBlock>("TelemetryText")!;
        _engineStateText = this.FindControl<TextBlock>("EngineStateText")!;
        _saveStateText = this.FindControl<TextBlock>("SaveStateText")!;
        _timelineHeader = this.FindControl<TextBlock>("TimelineHeader")!;
        _undoMenuItem = this.FindControl<MenuItem>("UndoMenuItem")!;
        _redoMenuItem = this.FindControl<MenuItem>("RedoMenuItem")!;
        _exportButton = this.FindControl<Button>("ExportButton")!;
        _maxButton = this.FindControl<Button>("MaxButton")!;

        // Reopen the way the user left it: centred (WindowStartupLocation in XAML) unless they had it maximized.
        WindowState = WindowStateStore.Load();

        WireWindowChrome();
        WireMenu();
        WireCommandMenus();
        PopulateProjectChrome(status);
        WireMediaBrowser();
        WireInspector();

        _history.Changed += OnHistoryChanged;
        OnHistoryChanged(); // initialise menu-enable + save-state

        if (_engine is null)
        {
            // No media: the shell still renders; just disable the live controls.
            SetEnabled(false);
            return;
        }

        WireTransport();
    }

    // ── Window chrome (frameless): drag, double-click maximize, min/max/close ──────────────────────

    private void WireWindowChrome()
    {
        var titleBar = this.FindControl<Border>("TitleBar")!;
        titleBar.PointerPressed += (_, e) =>
        {
            // The menu bar lives inside this draggable title-bar Border, and a drop-down item's press
            // bubbles up the logical tree through the Menu to here. Starting a window move-drag for it would
            // call Pointer.Capture(null) and tear down the capture the menu needs to deliver the click — so
            // every menu command would silently do nothing (keyboard accelerators were unaffected). Only the
            // bare caption is draggable: skip the drag for any press that originates within the menu.
            if (e.Source is ILogical src && src.GetSelfAndLogicalAncestors().OfType<Menu>().Any())
                return;
            if (e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        titleBar.DoubleTapped += (_, _) => ToggleMaximize();

        this.FindControl<Button>("MinButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        _maxButton!.Click += (_, _) => ToggleMaximize();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && _root is not null)
        {
            // In frameless (NoChrome) mode a maximized window extends past the work area by OffScreenMargin;
            // inset the content so nothing is clipped under the screen edges / taskbar.
            _root.Margin = WindowState == WindowState.Maximized ? OffScreenMargin : default;
            if (_maxButton is not null)
                _maxButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
            if (WindowState != WindowState.Minimized)
                _lastNonMinimizedState = WindowState; // remember the real state to persist (not a transient minimize)
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        WindowStateStore.Save(_lastNonMinimizedState); // remember maximized-or-not for next launch
        _thumbnails?.Dispose(); // releases the cached thumbnail bitmaps
        _ = _source?.DisposeAsync(); // tears down the Source monitor's decoder/engine if one is open
        base.OnClosed(e);
    }

    // ── Menu + keyboard ────────────────────────────────────────────────────────────────────────────

    private void WireMenu()
    {
        // File
        this.FindControl<MenuItem>("NewMenuItem")!.Click += (_, _) => NewProject();
        this.FindControl<MenuItem>("OpenMenuItem")!.Click += (_, _) => _ = OpenProjectAsync();
        this.FindControl<MenuItem>("SaveMenuItem")!.Click += (_, _) => Save();
        this.FindControl<MenuItem>("SaveAsMenuItem")!.Click += (_, _) => _ = SaveAsAsync();
        this.FindControl<MenuItem>("ImportMenuItem")!.Click += (_, _) => _ = ImportDialogAsync();
        this.FindControl<MenuItem>("ExportMenuItem")!.Click += (_, _) => _ = ExportAsync();
        this.FindControl<MenuItem>("ExitMenuItem")!.Click += (_, _) => Close();

        // Edit
        _undoMenuItem!.Click += (_, _) => _history.Undo();
        _redoMenuItem!.Click += (_, _) => _history.Redo();

        // Help
        this.FindControl<MenuItem>("AboutMenuItem")!.Click += (_, _) => _ = AboutDialog.Show(this);

        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// Wires the editing menus that act on the selection (Edit ▸ Cut/Copy/Paste/Delete, Clip ▸ Unlink/Nudge),
    /// builds the Effects menu from <see cref="EffectCatalog"/>, and connects the View / Window menus
    /// (PLAN.md step 16c). The handlers read <see cref="_timeline"/> lazily, so this can run before the timeline
    /// is attached. Context-enabling happens on submenu-open so the items reflect the current selection /
    /// clipboard without per-edit bookkeeping.
    /// </summary>
    private void WireCommandMenus()
    {
        // Toolbar toggles are the source of truth for Snapping / Guides; the View menu mirrors them.
        _snappingToggle = this.FindControl<ToggleButton>("SnappingToggle");
        _guidesToggle = this.FindControl<ToggleButton>("GuidesToggle");

        // Panes / grids for the View panel toggles + Window ▸ Reset Layout.
        _workspaceGrid = this.FindControl<Grid>("WorkspaceGrid");
        _outerGrid = this.FindControl<Grid>("OuterGrid");
        _projectPane = this.FindControl<Border>("ProjectPane");
        _projectSplitter = this.FindControl<GridSplitter>("ProjectSplitter");
        _inspectorPane = this.FindControl<Border>("InspectorPane");
        _inspectorSplitter = this.FindControl<GridSplitter>("InspectorSplitter");

        // ── Edit ──
        _cutMenuItem = this.FindControl<MenuItem>("CutMenuItem")!;
        _copyMenuItem = this.FindControl<MenuItem>("CopyMenuItem")!;
        _pasteMenuItem = this.FindControl<MenuItem>("PasteMenuItem")!;
        _deleteMenuItem = this.FindControl<MenuItem>("DeleteMenuItem")!;
        _cutMenuItem.Click += (_, _) => _timeline?.CutSelected();
        _copyMenuItem.Click += (_, _) => _timeline?.CopySelected();
        _pasteMenuItem.Click += (_, _) => _timeline?.PasteAtPlayhead();
        _deleteMenuItem.Click += (_, _) => _timeline?.DeleteSelected();
        this.FindControl<MenuItem>("EditMenu")!.SubmenuOpened += (_, _) => RefreshEditMenu();

        // ── Clip ──
        _unlinkMenuItem = this.FindControl<MenuItem>("ClipUnlinkMenuItem")!;
        _nudgeLeftMenuItem = this.FindControl<MenuItem>("NudgeLeftMenuItem")!;
        _nudgeRightMenuItem = this.FindControl<MenuItem>("NudgeRightMenuItem")!;
        _unlinkMenuItem.Click += (_, _) => _timeline?.UnlinkSelected();
        _nudgeLeftMenuItem.Click += (_, _) => _timeline?.NudgeSelected(-1);
        _nudgeRightMenuItem.Click += (_, _) => _timeline?.NudgeSelected(+1);
        this.FindControl<MenuItem>("ClipMenu")!.SubmenuOpened += (_, _) => RefreshClipMenu();

        // ── Effects (populated from the registry, PLAN.md steps 15–16) ──
        var effectsMenu = this.FindControl<MenuItem>("EffectsMenu")!;
        var effectItems = new System.Collections.Generic.List<MenuItem>();
        foreach (EffectDescriptor descriptor in EffectCatalog.BuiltIns)
        {
            string id = descriptor.Id; // capture per iteration
            var item = new MenuItem { Header = descriptor.DisplayName };
            item.Click += (_, _) => _timeline?.ApplyEffectToSelected(id);
            effectItems.Add(item);
        }
        effectsMenu.ItemsSource = effectItems;
        _effectsMenuItems = effectItems;
        effectsMenu.SubmenuOpened += (_, _) => RefreshEffectsMenu();

        // ── View ──
        this.FindControl<MenuItem>("ZoomInMenuItem")!.Click += (_, _) => _timeline?.ZoomIn();
        this.FindControl<MenuItem>("ZoomOutMenuItem")!.Click += (_, _) => _timeline?.ZoomOut();
        _snappingMenuItem = this.FindControl<MenuItem>("SnappingMenuItem")!;
        _guidesMenuItem = this.FindControl<MenuItem>("GuidesMenuItem")!;
        _showProjectMenuItem = this.FindControl<MenuItem>("ShowProjectMenuItem")!;
        _showInspectorMenuItem = this.FindControl<MenuItem>("ShowInspectorMenuItem")!;
        // Drive the toolbar toggle (the single source of truth) from the menu checkbox's new state.
        _snappingMenuItem.Click += (_, _) => { if (_snappingToggle is not null) _snappingToggle.IsChecked = _snappingMenuItem.IsChecked; };
        _guidesMenuItem.Click += (_, _) => { if (_guidesToggle is not null) _guidesToggle.IsChecked = _guidesMenuItem.IsChecked; };
        _showProjectMenuItem.Click += (_, _) => SetPanelVisible(project: true, _showProjectMenuItem.IsChecked == true);
        _showInspectorMenuItem.Click += (_, _) => SetPanelVisible(project: false, _showInspectorMenuItem.IsChecked == true);
        this.FindControl<MenuItem>("ViewMenu")!.SubmenuOpened += (_, _) => RefreshViewMenu();

        // ── Window ──
        this.FindControl<MenuItem>("ResetLayoutMenuItem")!.Click += (_, _) => ResetLayout();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // ── Global accelerators (work regardless of focus) ──
        if (ctrl && e.Key == Key.N) { NewProject(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { _ = OpenProjectAsync(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { _ = SaveAsAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.E) { _ = ExportAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.I) { _ = ImportDialogAsync(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Z) { _history.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { _history.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _history.Redo(); e.Handled = true; return; }

        // Below here are transport / editing keys that must not steal input from a focused text field
        // (the media-bin search box, the Inspector numeric boxes).
        if (IsTypingInTextBox())
            return;

        if (ctrl && e.Key == Key.X) { _timeline?.CutSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { _timeline?.CopySelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { _timeline?.PasteAtPlayhead(); e.Handled = true; }
        else if (e.Key == Key.Delete || e.Key == Key.Back) { _timeline?.DeleteSelected(); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { _timeline?.NudgeSelected(-1); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { _timeline?.NudgeSelected(+1); e.Handled = true; }
        // Jump-to-previous/next-keyframe of the selected clip (Premiere uses [ / ], step 16d).
        else if (e.Key == Key.OemOpenBrackets) { JumpToKeyframe(-1); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { JumpToKeyframe(+1); e.Handled = true; }
        else if (e.Key == Key.Space) { _active?.TogglePlayPause(); e.Handled = true; }
    }

    private bool IsTypingInTextBox() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// Seeks the playhead to the previous (<paramref name="direction"/> &lt; 0) or next keyframe of the selected
    /// clip, across all its animated parameters (PLAN.md step 16d). A no-op when there is no selection or no
    /// keyframe in that direction. Keyframe times are absolute timeline times, so this drives the Program
    /// monitor (the timeline), regardless of which monitor is active.
    /// </summary>
    private void JumpToKeyframe(int direction)
    {
        if (_selectedClip is not { } clip || _program is null)
            return;
        Timecode now = _program.Position;
        Timecode? target = direction < 0
            ? KeyframeNavigation.PreviousKeyframe(clip, now)
            : KeyframeNavigation.NextKeyframe(clip, now);
        if (target is { } t)
            _program.SeekTo(t);
    }

    /// <summary>Enables the keyframe-jump buttons only when the selection has keyframes to navigate.</summary>
    private void RefreshKeyframeNav()
    {
        bool has = _selectedClip is { } clip && KeyframeNavigation.HasKeyframes(clip);
        if (_prevKeyframeButton is not null)
            _prevKeyframeButton.IsEnabled = has;
        if (_nextKeyframeButton is not null)
            _nextKeyframeButton.IsEnabled = has;
    }

    // ── Project chrome: title, media bin, sequence badge, telemetry ─────────────────────────────────

    private void PopulateProjectChrome(string status)
    {
        SetStatus(status);

        if (_project is null)
            return;

        if (_currentProjectPath is not null)
            _projectName = ProjectDisplayName(_currentProjectPath);
        else
        {
            string? mediaPath = _project.MediaPool.Items.FirstOrDefault()?.AbsolutePath;
            _projectName = mediaPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(mediaPath);
        }
        UpdateProjectTitle();

        Timeline timeline = _project.Timeline;
        double fps = Fps(timeline.FrameRate);
        (int w, int h) = (timeline.Resolution.Width, timeline.Resolution.Height);
        string resLabel = h switch { 2160 => "4K", 1080 => "1080p", 720 => "720p", _ => $"{w}×{h}" };
        this.FindControl<TextBlock>("SequenceBadge")!.Text = $"{resLabel} · {fps:0.##}";

        Timecode duration = _engine?.Duration ?? timeline.Duration;
        _telemetryText!.Text = $"{fps:0.##} fps · {w}×{h} · {FormatTime(duration)}";

        UpdateTimelineHeader();
    }

    private void UpdateProjectTitle() => this.FindControl<TextBlock>("ProjectTitleText")!.Text = _projectName;

    private void UpdateTimelineHeader()
    {
        if (_project is null)
            return;
        int tracks = _project.Timeline.Tracks.Count;
        _timelineHeader!.Text = $"Timeline · {_projectName} · {tracks} track{(tracks == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Binds the Project panel's tabbed media browser (PLAN.md step 15): the media bin (poster/waveform
    /// thumbnails + badges + search), the Effects browser (double-click adds to the selected clip via the
    /// command stack), and the Audio tab. The browser reports its item count to the pane header and routes
    /// hints to the status strip. Independent of playback, so the bin works even when no engine is available.
    /// </summary>
    private void WireMediaBrowser()
    {
        if (_project is null || this.FindControl<MediaBrowserPanel>("MediaBrowser") is not { } browser)
            return;

        _mediaBrowser = browser;
        _thumbnails = new ThumbnailService();

        var itemsText = this.FindControl<TextBlock>("ProjectItemsText")!;
        browser.ItemCountChanged += n => itemsText.Text = n == 1 ? "1 item" : $"{n} items";
        browser.Status += SetStatus;
        browser.FilesDropped += paths => Import(paths); // OS file-drop onto the bin (PLAN.md step 16b)
        browser.Attach(_project, _history, _thumbnails);
    }

    /// <summary>
    /// Binds the type-driven Inspector (PLAN.md step 16): the selected clip's Clip section + one section per
    /// effect, each built from the effect's parameter descriptors with slider/numeric editing and keyframe
    /// affordances, all through the command stack. The playhead accessor lets animated values display (and
    /// keyframe in) at the current time. Independent of playback, so it works even with no engine.
    /// </summary>
    private void WireInspector()
    {
        if (_project is null || this.FindControl<InspectorPanel>("Inspector") is not { } inspector)
            return;

        _inspector = inspector;
        inspector.Attach(_project, _history, () => _engine?.Position ?? Timecode.Zero);
    }

    // ── Transport ───────────────────────────────────────────────────────────────────────────────────

    private void WireTransport()
    {
        _playPause = this.FindControl<Button>("PlayPauseButton")!;
        _scrubber = this.FindControl<Slider>("Scrubber")!;
        _positionText = this.FindControl<TextBlock>("PositionText")!;
        _durationText = this.FindControl<TextBlock>("DurationText")!;

        // The Program monitor composites the timeline at the sequence resolution; the Source monitor previews a
        // single selected clip's source (built lazily when its tab is opened). Both present through the one shared
        // surface; the active tab decides which engine is attached to it.
        _preview = this.FindControl<PreviewSurface>("Preview")!;
        (int seqW, int seqH) = (_project!.Timeline.Resolution.Width, _project.Timeline.Resolution.Height);
        _program = new ProgramMonitor(_engine!, seqW, seqH);
        _source = new SourceMonitor();
        _active = _program;

        // Re-bind the surface whenever the active monitor's engine is replaced (the Source monitor rebuilds).
        _source.EngineChanged += () => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_active, _source)) BindActiveToSurface(); });
        BindActiveToSurface(); // attach the program engine to the surface

        WireTimeline();
        WireMonitorTabs();
        WireZoomAndGuides();

        _exportButton!.Click += (_, _) => _ = ExportAsync();
        WireAddTrackButton();

        // Transport buttons drive the active monitor.
        _playPause.Click += (_, _) => _active!.TogglePlayPause();
        this.FindControl<Button>("JumpStartButton")!.Click += (_, _) => _active!.JumpToStart();
        this.FindControl<Button>("JumpEndButton")!.Click += (_, _) => _active!.JumpToEnd();
        this.FindControl<Button>("StepBackButton")!.Click += (_, _) => _active!.StepFrame(-1);
        this.FindControl<Button>("StepForwardButton")!.Click += (_, _) => _active!.StepFrame(+1);
        _prevKeyframeButton = this.FindControl<Button>("PrevKeyframeButton")!;
        _nextKeyframeButton = this.FindControl<Button>("NextKeyframeButton")!;
        _prevKeyframeButton.Click += (_, _) => JumpToKeyframe(-1);
        _nextKeyframeButton.Click += (_, _) => JumpToKeyframe(+1);
        RefreshKeyframeNav();

        _scrubber.ValueChanged += (_, e) =>
        {
            if (_suppressSeek)
                return;
            _active!.SeekTo(new Timecode((long)e.NewValue));
        };

        // Both monitors report position/state; the readouts update only for the active one. The Inspector tracks
        // the Program playhead specifically (its keyframes edit the timeline at that time).
        WireMonitorReadouts(_program, isProgram: true);
        WireMonitorReadouts(_source, isProgram: false);
        RefreshTransportForActive();

        // A pump iteration can fault (e.g. the audio device hiccupping during the end-of-timeline stop); the
        // engine keeps the transport alive rather than dying, so surface the reason instead of swallowing it.
        _engine!.PumpError += ex => Dispatcher.UIThread.Post(() => SetStatus($"Playback recovered from an error: {ex.Message}"));

        // Optional timed auto-exit for unattended profiling runs: SPROCKET_APP_SECONDS=12
        if (int.TryParse(Environment.GetEnvironmentVariable("SPROCKET_APP_SECONDS"), out int seconds) && seconds > 0)
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Routes a monitor's position/state events to the transport readouts, but only while it is the
    /// active monitor. The Program monitor additionally drives the Inspector's playhead.</summary>
    private void WireMonitorReadouts(IMonitor monitor, bool isProgram)
    {
        monitor.PositionChanged += pos => Dispatcher.UIThread.Post(() =>
        {
            if (isProgram)
                _inspector?.OnPlayheadMoved(); // animated parameter values track the Program playhead
            if (!ReferenceEquals(_active, monitor))
                return;
            _suppressSeek = true;
            _scrubber!.Value = Math.Clamp(pos.Ticks, 0, _scrubber.Maximum);
            _suppressSeek = false;
            _positionText!.Text = FormatTime(pos);
        });

        monitor.StateChanged += state => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_active, monitor))
                return;
            _playPause!.Content = state == PlaybackState.Playing ? "❚❚" : "▶";
            _engineStateText!.Text = state == PlaybackState.Playing ? "Playing" : "Paused";
        });
    }

    /// <summary>Attaches the active monitor's current engine to the shared surface at its logical frame size, or
    /// clears the surface if the monitor has nothing loaded.</summary>
    private void BindActiveToSurface()
    {
        if (_active!.CurrentEngine is { } engine)
        {
            _preview!.SetFrameSize(_active.FrameWidth, _active.FrameHeight);
            _preview.Attach(engine);
        }
        else
        {
            _preview!.Detach();
        }
    }

    /// <summary>Switches between the Program and Source monitors (UI.md §3.4): pauses the outgoing monitor,
    /// builds/frees the Source engine, re-binds the shared surface, and re-points the transport readouts.</summary>
    private void WireMonitorTabs()
    {
        var programTab = this.FindControl<RadioButton>("ProgramTab")!;
        var sourceTab = this.FindControl<RadioButton>("SourceTab")!;

        programTab.IsCheckedChanged += (_, _) =>
        {
            if (programTab.IsChecked != true)
                return;
            _source!.Deactivate();
            _active = _program!;
            BindActiveToSurface();
            RefreshTransportForActive();
        };

        sourceTab.IsCheckedChanged += (_, _) =>
        {
            if (sourceTab.IsChecked != true)
                return;
            _program!.Pause();
            _active = _source!;
            _source!.Activate(); // raises EngineChanged → binds the surface
            BindActiveToSurface();
            RefreshTransportForActive();
        };
    }

    /// <summary>Binds the <c>Fit ▾</c> zoom level and the safe-area/framing-grid toggle to the shared surface.</summary>
    private void WireZoomAndGuides()
    {
        var zoomBox = this.FindControl<ComboBox>("ZoomBox")!;
        zoomBox.SelectionChanged += (_, _) =>
        {
            _preview!.Zoom = zoomBox.SelectedIndex switch
            {
                1 => MonitorZoom.Percent50,
                2 => MonitorZoom.Percent100,
                3 => MonitorZoom.Percent200,
                _ => MonitorZoom.Fit,
            };
        };

        if (_guidesToggle is not null)
            _guidesToggle.IsCheckedChanged += (_, _) => _preview!.ShowGuides = _guidesToggle.IsChecked == true;
    }

    /// <summary>Re-points the transport readouts (scrubber range, position/duration text, play glyph, state) at
    /// the currently active monitor — after a tab switch or a Source-clip change.</summary>
    private void RefreshTransportForActive()
    {
        IMonitor m = _active!;
        _suppressSeek = true;
        _scrubber!.Maximum = Math.Max(1, m.Duration.Ticks);
        _scrubber.Value = Math.Clamp(m.Position.Ticks, 0, _scrubber.Maximum);
        _suppressSeek = false;
        _positionText!.Text = FormatTime(m.Position);
        _durationText!.Text = FormatTime(m.Duration);
        _playPause!.Content = m.State == PlaybackState.Playing ? "❚❚" : "▶";
        _engineStateText!.Text = m.State == PlaybackState.Playing ? "Playing" : "Paused";
    }

    /// <summary>
    /// Binds the custom timeline control to the project / edit history / engine and connects the timeline's
    /// chrome (zoom buttons, the Snapping toggle) and its selection back to the shell.
    /// </summary>
    private void WireTimeline()
    {
        var timeline = this.FindControl<TimelineControl>("Timeline")!;
        _timeline = timeline;
        timeline.Attach(_project!, _history, _engine);
        timeline.ClipPlaced += UpdateTimelineHeader; // a media-bin drop / paste may extend the timeline

        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => timeline.ZoomIn();
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => timeline.ZoomOut();

        if (_snappingToggle is not null)
        {
            timeline.Snapping = _snappingToggle.IsChecked == true;
            _snappingToggle.IsCheckedChanged += (_, _) => timeline.Snapping = _snappingToggle.IsChecked == true;
        }

        var linked = this.FindControl<ToggleButton>("LinkedToggle")!;
        timeline.Linked = linked.IsChecked == true;
        linked.IsCheckedChanged += (_, _) => timeline.Linked = linked.IsChecked == true;

        // Tool palette (radio group): each button selects the matching timeline tool.
        WireTool("SelectTool", EditTool.Select, timeline);
        WireTool("BladeTool", EditTool.Blade, timeline);
        WireTool("SlipTool", EditTool.Slip, timeline);
        WireTool("HandTool", EditTool.Hand, timeline);
        WireTool("ZoomTool", EditTool.Zoom, timeline);

        timeline.SelectedClipChanged += clip =>
        {
            _selectedClip = clip;
            _mediaBrowser?.SetSelectedClip(clip); // the Effects browser applies to this clip
            _inspector?.SetSelectedClip(clip);    // the Inspector edits this clip's properties
            RefreshKeyframeNav();

            // The Source monitor previews the selected clip's source (rebuilds lazily only while its tab is open).
            MediaRef? media = clip is null ? null : _project!.MediaPool.Get(clip.MediaRefId);
            _source?.SetSource(media);
            if (ReferenceEquals(_active, _source))
                RefreshTransportForActive();

            string? name = media is null ? null : Path.GetFileName(media.AbsolutePath ?? "clip");
            SetStatus(name is null ? "" : $"Selected: {name}");
        };
    }

    /// <summary>Binds a tool-palette radio button to its <see cref="EditTool"/> on the timeline.</summary>
    private void WireTool(string name, EditTool tool, TimelineControl timeline)
    {
        var button = this.FindControl<RadioButton>(name)!;
        if (button.IsChecked == true)
            timeline.ActiveTool = tool;
        button.IsCheckedChanged += (_, _) =>
        {
            if (button.IsChecked == true)
            {
                timeline.ActiveTool = tool;
                SetStatus($"{tool} tool");
            }
        };
    }

    /// <summary>Attaches a flyout to <c>+ Track</c> offering a new Video or Audio track (PLAN.md step 14).</summary>
    private void WireAddTrackButton()
    {
        var button = this.FindControl<Button>("AddTrackButton")!;
        var videoItem = new MenuItem { Header = "Add _Video Track" };
        videoItem.Click += (_, _) => AddTrack(video: true);
        var audioItem = new MenuItem { Header = "Add _Audio Track" };
        audioItem.Click += (_, _) => AddTrack(video: false);
        button.Flyout = new MenuFlyout { ItemsSource = new[] { videoItem, audioItem } };
    }

    /// <summary>
    /// Adds a video or audio track through an <see cref="AddTrackCommand"/> (PLAN.md step 14), so it is undoable
    /// and the dirty indicator flips. The new track is appended on top in z-order; the playback engine and mixer
    /// pick it up live. Tracks are auto-numbered per kind (V1/V2…, A1/A2…).
    /// </summary>
    private void AddTrack(bool video)
    {
        if (_project is null)
            return;
        Sprocket.Core.Model.Track track = video
            ? new VideoTrack { Name = $"V{_project.Timeline.VideoTracks.Count() + 1}" }
            : new AudioTrack { Name = $"A{_project.Timeline.AudioTracks.Count() + 1}" };
        _history.Execute(new AddTrackCommand(_project.Timeline, track));
    }

    // ── Context-enabling: refresh menu items on submenu open ────────────────────────────────────────

    private void RefreshEditMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        if (_cutMenuItem is not null) _cutMenuItem.IsEnabled = sel;
        if (_copyMenuItem is not null) _copyMenuItem.IsEnabled = sel;
        if (_pasteMenuItem is not null) _pasteMenuItem.IsEnabled = _timeline?.CanPaste == true;
        if (_deleteMenuItem is not null) _deleteMenuItem.IsEnabled = sel;
    }

    private void RefreshClipMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        if (_unlinkMenuItem is not null) _unlinkMenuItem.IsEnabled = _timeline?.SelectedIsLinked == true;
        if (_nudgeLeftMenuItem is not null) _nudgeLeftMenuItem.IsEnabled = sel;
        if (_nudgeRightMenuItem is not null) _nudgeRightMenuItem.IsEnabled = sel;
    }

    private void RefreshEffectsMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        foreach (MenuItem item in _effectsMenuItems)
            item.IsEnabled = sel;
    }

    private void RefreshViewMenu()
    {
        if (_snappingMenuItem is not null) _snappingMenuItem.IsChecked = _snappingToggle?.IsChecked == true;
        if (_guidesMenuItem is not null) _guidesMenuItem.IsChecked = _guidesToggle?.IsChecked == true;
        if (_showProjectMenuItem is not null) _showProjectMenuItem.IsChecked = _projectPane?.IsVisible != false;
        if (_showInspectorMenuItem is not null) _showInspectorMenuItem.IsChecked = _inspectorPane?.IsVisible != false;
    }

    // ── View / Window: panel visibility + layout ────────────────────────────────────────────────────

    /// <summary>Shows or hides the Project (left) or Inspector (right) pane by collapsing its column + splitter.</summary>
    private void SetPanelVisible(bool project, bool visible)
    {
        if (_workspaceGrid is null)
            return;
        if (project)
        {
            if (_projectPane is not null) _projectPane.IsVisible = visible;
            if (_projectSplitter is not null) _projectSplitter.IsVisible = visible;
            _workspaceGrid.ColumnDefinitions[0].Width = visible ? new GridLength(240) : new GridLength(0);
            _workspaceGrid.ColumnDefinitions[1].Width = visible ? new GridLength(6) : new GridLength(0);
        }
        else
        {
            if (_inspectorPane is not null) _inspectorPane.IsVisible = visible;
            if (_inspectorSplitter is not null) _inspectorSplitter.IsVisible = visible;
            _workspaceGrid.ColumnDefinitions[4].Width = visible ? new GridLength(300) : new GridLength(0);
            _workspaceGrid.ColumnDefinitions[3].Width = visible ? new GridLength(6) : new GridLength(0);
        }
    }

    /// <summary>Window ▸ Reset Layout: restore the pane splitters to their default sizes and show all panes.</summary>
    private void ResetLayout()
    {
        if (_workspaceGrid is not null)
        {
            _workspaceGrid.ColumnDefinitions[0].Width = new GridLength(240);
            _workspaceGrid.ColumnDefinitions[1].Width = new GridLength(6);
            _workspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _workspaceGrid.ColumnDefinitions[3].Width = new GridLength(6);
            _workspaceGrid.ColumnDefinitions[4].Width = new GridLength(300);
        }
        if (_outerGrid is not null)
            _outerGrid.RowDefinitions[2].Height = new GridLength(240);
        SetPanelVisible(project: true, true);
        SetPanelVisible(project: false, true);
        SetStatus("Layout reset.");
    }

    // ── Edit-history reactions: menu enable + dirty indicator ───────────────────────────────────────

    private void OnHistoryChanged()
    {
        _undoMenuItem!.IsEnabled = _history.CanUndo;
        _redoMenuItem!.IsEnabled = _history.CanRedo;
        _undoMenuItem.Header = _history.CanUndo ? $"_Undo {_history.UndoLabel}" : "_Undo";
        _redoMenuItem.Header = _history.CanRedo ? $"_Redo {_history.RedoLabel}" : "_Redo";

        bool dirty = _history.UndoCount != _savedUndoCount;
        _saveStateText!.Text = dirty ? "• unsaved changes" : "• all changes saved";
        UpdateTimelineHeader();
        RefreshKeyframeNav(); // a keyframe just added/removed on the selection toggles the jump buttons

        // A timeline edit (placement, trim, delete) can change the overall duration, so re-point the scrubber
        // range / duration readout. Only the Program monitor reflects the timeline; the Source monitor spans
        // its own media. (_active is non-null only once the transport is wired.)
        if (_active is not null && ReferenceEquals(_active, _program))
            RefreshTransportForActive();
    }

    // ── Import ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a file picker and imports the chosen media into the project's <see cref="Core.Model.MediaPool"/>
    /// (PLAN.md step 16b). Each file is probed + added through the command stack (undoable); the bin refreshes
    /// so the imported source's thumbnail/badges appear (step 15). OS file-drop onto the bin shares
    /// <see cref="Import"/>.
    /// </summary>
    private async Task ImportDialogAsync()
    {
        if (_project is null)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Media",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Media files")
                {
                    Patterns = ["*.mp4", "*.mov", "*.mkv", "*.m4v", "*.avi", "*.webm", "*.wav", "*.mp3", "*.aac", "*.flac", "*.m4a"],
                },
                FilePickerFileTypes.All,
            ],
        });

        var paths = new List<string>();
        foreach (IStorageFile file in files)
            if (file.TryGetLocalPath() is { } path)
                paths.Add(path);
        if (paths.Count > 0)
            Import(paths);
    }

    /// <summary>Imports the given paths into the project and refreshes the media bin (PLAN.md step 16b).</summary>
    private void Import(IReadOnlyList<string> paths)
    {
        if (_project is null)
            return;

        int added = 0;
        var failures = new List<(string Name, string Reason)>();
        foreach (string path in paths)
        {
            MediaImport.Result result = MediaImport.TryImport(_project, _history, path);
            if (result.Succeeded)
                added++;
            else
                failures.Add((Path.GetFileName(path), result.Error ?? "could not open"));
        }

        _mediaBrowser?.Refresh();

        if (failures.Count == 0)
            SetStatus($"Imported {added} file{(added == 1 ? "" : "s")}.");
        else if (paths.Count == 1)
            SetStatus($"Could not import {failures[0].Name}: {failures[0].Reason}");
        else
        {
            string detail = $"{failures[0].Name}: {failures[0].Reason}"
                + (failures.Count > 1 ? $" (+{failures.Count - 1} more)" : "");
            SetStatus($"Imported {added}, {failures.Count} failed — {detail}");
        }
    }

    // ── File ops (New / Open / Save / Save As) ──────────────────────────────────────────────────────

    /// <summary>File ▸ New: after confirming any unsaved changes, requests a fresh empty project (one video +
    /// one audio track) from the composition root (PLAN.md step 16c).</summary>
    private async void NewProject()
    {
        if (!await ConfirmDiscardIfDirty())
            return;

        var project = new Project();
        project.Timeline.Tracks.Add(new VideoTrack { Name = "V1" });
        project.Timeline.Tracks.Add(new AudioTrack { Name = "A1" });
        SessionRequested?.Invoke(new SessionRequest(project, "New project", null));
    }

    /// <summary>File ▸ Open: after confirming unsaved changes, loads a project JSON and requests a session over
    /// it. Load is offline-tolerant (§15); a parse/schema error is surfaced rather than thrown at the user.</summary>
    private async Task OpenProjectAsync()
    {
        if (!await ConfirmDiscardIfDirty())
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Project",
            AllowMultiple = false,
            FileTypeFilter = [SprocketProjectFileType, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        try
        {
            Project project = ProjectSerializer.Load(path);
            SessionRequested?.Invoke(new SessionRequest(project, $"Opened {Path.GetFileName(path)}", path));
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}");
        }
    }

    /// <summary>
    /// File ▸ Save: writes to the project's current file, or prompts (Save As) when it has never been saved.
    /// The actual serialization lives in <see cref="ProjectSerializer"/> (PLAN.md step 9).
    /// </summary>
    private void Save()
    {
        if (_project is null)
            return;
        if (_currentProjectPath is null)
        {
            _ = SaveAsAsync();
            return;
        }
        SaveTo(_currentProjectPath);
    }

    /// <summary>
    /// File ▸ Save As: writes the current project to a newly chosen file as an independent copy — the original
    /// file (if any) is left untouched — and re-points the document at the new file.
    /// </summary>
    private async Task SaveAsAsync()
    {
        if (_project is null)
            return;

        string suggested = (_currentProjectPath is null ? _projectName : ProjectDisplayName(_currentProjectPath)) + ".sprocket.json";
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Project As",
            SuggestedFileName = suggested,
            DefaultExtension = "json",
            FileTypeChoices = [SprocketProjectFileType],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;

        _currentProjectPath = path;
        _projectName = ProjectDisplayName(path);
        UpdateProjectTitle();
        SaveTo(path);
    }

    private void SaveTo(string path)
    {
        try
        {
            ProjectSerializer.Save(_project!, path);
            _savedUndoCount = _history.UndoCount;
            OnHistoryChanged(); // refresh the dirty indicator
            SetStatus($"Saved → {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    /// <summary>Asks the user to confirm discarding unsaved changes; returns <c>true</c> to proceed. A clean
    /// document proceeds without prompting.</summary>
    private async Task<bool> ConfirmDiscardIfDirty()
    {
        if (_history.UndoCount == _savedUndoCount)
            return true;
        return await ConfirmDialog.Show(
            this, "Unsaved changes",
            "This project has unsaved changes that will be lost. Discard them and continue?",
            "Discard", "Cancel");
    }

    /// <summary>The display name for a project file (drops the <c>.sprocket.json</c> / <c>.json</c> suffix).</summary>
    private static string ProjectDisplayName(string path)
    {
        string name = Path.GetFileName(path);
        const string sprocketExt = ".sprocket.json";
        if (name.EndsWith(sprocketExt, StringComparison.OrdinalIgnoreCase))
            return name[..^sprocketExt.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// Exports the loaded project to an <c>.mp4</c> next to the app output on a background thread (export is
    /// CPU-bound and must not block the UI), pausing playback first and streaming progress to the status strip.
    /// The actual pipeline lives in <see cref="VideoExporter"/>; this is just the composition-root trigger.
    /// </summary>
    private async Task ExportAsync()
    {
        if (_exporting || _project is null)
            return;

        _exporting = true;
        _exportButton!.IsEnabled = false;
        _engine?.Pause();

        string outputPath = Path.Combine(AppContext.BaseDirectory, "export.mp4");
        var progress = new Progress<double>(p => SetStatus($"Exporting… {p * 100:0}%"));

        try
        {
            await Task.Run(() => VideoExporter.Export(_project, outputPath, progress: progress));
            SetStatus($"Exported → {outputPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
            _exportButton!.IsEnabled = true;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private void SetEnabled(bool enabled)
    {
        foreach (string name in new[] { "PlayPauseButton", "JumpStartButton", "JumpEndButton", "StepBackButton", "StepForwardButton", "Scrubber", "AddTrackButton" })
            if (this.FindControl<Control>(name) is { } c)
                c.IsEnabled = enabled;
        if (_exportButton is not null)
            _exportButton.IsEnabled = enabled;
    }

    private void SetStatus(string text)
    {
        if (_statusText is not null)
            _statusText.Text = text;
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;

    private static string FormatTime(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
