using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sprocket.App.Inspector;
using Sprocket.App.MediaBrowser;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Persistence;
using Sprocket.Persistence.Interchange;
using Sprocket.Playback;
using Sprocket.Render;
using Ellipse = Avalonia.Controls.Shapes.Ellipse; // aliased so it doesn't drag Shapes.Path into scope (clashes with System.IO.Path)

namespace Sprocket.App;

/// <summary>
/// The editor shell (PLAN.md step 11, UI.md §1/§2): a frameless window with custom chrome + inline menu bar,
/// splitter-resizable Project / Program / Inspector panes over a full-width Timeline, and a status bar with
/// live telemetry. The full menu / command surface is wired in step 16c — File (New / Open / Open Sample /
/// Save / Save As / Import / Export / Exit), Edit (undo/redo + clip cut/copy/paste/delete), Clip (unlink / nudge), Effects
/// (apply from the catalog), View (zoom / snapping / guides / panel toggles), Window (reset layout) and Help
/// (About) — every editing action routing through <see cref="EditHistory"/> so it stays undoable. Items whose
/// feature lands in a later step (Sequence, per-clip Enable/Speed, Select All, Link) stay visibly disabled.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlaybackEngine? _engine;
    private readonly Project? _project;
    private readonly Proxy.ProxyService? _proxy; // session proxy service (PLAN.md step 18); owned by App
    private readonly Sprocket.Audio.AudioEngine? _audioClock; // live loudness source for the mixer meters (PLAN.md step 30); owned by the engine
    private readonly EditHistory _history = new();
    private AutosaveService? _autosave; // periodic debounced autosave (PLAN.md step 20)

    private ThumbnailService? _thumbnails;
    private MediaBrowserPanel? _mediaBrowser;
    private Mixer.MixerView? _mixer; // audio mixer hosted in the Project panel's Audio tab (PLAN.md step 30)
    private InspectorPanel? _inspector;
    private TimelineControl? _timeline;
    private Clip? _selectedClip; // the timeline selection (keyframe navigation targets its keyframes, step 16d)

    // Inline track-rename editor (overlaid on the timeline): the TextBox and the track being renamed.
    private TextBox? _trackRenameEditor;
    private Sprocket.Core.Model.Track? _renameTarget;

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
    private Export.ExportQueue? _exportQueue;         // lazily built on first Export Queue use (PLAN.md step 29)
    private ExportQueueWindow? _exportQueueWindow;    // the (reused) queue window, or null when closed
    private int _savedUndoCount;       // history depth at the last save; document is clean while it matches
    private string _projectName = "Untitled";
    private string? _currentProjectPath; // the file this project was loaded from / last saved to (null = untitled)

    // Controls captured for later updates.
    private TextBlock? _statusText, _telemetryText, _engineStateText, _saveStateText, _timelineHeader;
    private Ellipse? _stateDot;

    // Status-bar telemetry (PLAN.md step 29, UI.md §3.7). The live readout (state + GPU/hw-accel + fps) is polled
    // on a slow UI timer, and — critically for the no-per-frame-work rule (ARCHITECTURE.md §1) — the timer runs
    // ONLY while playing: it reads the engine's existing cumulative counters (a couple of Interlocked reads) at 1 Hz
    // and does zero work on the render/decode hot path. At idle it is stopped and the readout settles to the nominal
    // sequence rate on the state-change event, so a paused editor incurs no periodic wake-ups.
    private DispatcherTimer? _telemetryTimer;
    private long _prevPresented, _prevStatsTs; // baseline for the live-fps delta (frames presented, Stopwatch ticks)
    private MenuItem? _undoMenuItem, _redoMenuItem;
    private Button? _exportButton, _maxButton;
    private Control? _root;
    private WindowState _lastNonMinimizedState = WindowState.Normal; // persisted on close (ignores Minimized)

    // Command-menu items refreshed on submenu open (context-enabling) + the View toggles / panes.
    private MenuItem? _cutMenuItem, _copyMenuItem, _pasteMenuItem, _deleteMenuItem, _rippleDeleteMenuItem;
    private MenuItem? _unlinkMenuItem, _nudgeLeftMenuItem, _nudgeRightMenuItem, _clipSpeedMenuItem;
    private MenuItem? _createMulticamMenuItem; // Clip ▸ Create Multicam Source (PLAN.md step 24)
    private MenuItem? _clipNormalizeMenuItem;  // Clip ▸ Normalize Audio (PLAN.md step 30)
    private MenuItem? _nestMenuItem, _openSequenceMenuItem; // Sequence menu (PLAN.md step 23)
    private MenuItem? _snappingMenuItem, _guidesMenuItem, _showProjectMenuItem, _showInspectorMenuItem, _showStatsMenuItem;
    private PlaybackStatsOverlay? _statsOverlay; // floating playback-diagnostics window (View ▸ Playback Statistics)
    private System.Collections.Generic.IReadOnlyList<MenuItem> _effectsMenuItems = [];
    private ToggleButton? _snappingToggle, _guidesToggle;
    private Grid? _workspaceGrid, _outerGrid;
    private Border? _projectPane, _inspectorPane;
    private GridSplitter? _projectSplitter, _inspectorSplitter;

    /// <summary>The Sprocket project file type (a JSON sidecar) for the open / save-as pickers.</summary>
    private static readonly FilePickerFileType SprocketProjectFileType =
        new("Sprocket project") { Patterns = ["*.sprocket.json", "*.json"] };

    /// <summary>Import file-type filters for the media open dialog (PLAN.md step 27 import coverage): the common
    /// containers and audio-only formats the FFmpeg 8 decode path handles. Unsupported / corrupt files still fail
    /// gracefully per file (see <see cref="Import"/>), so the "All files" fall-through stays available.</summary>
    private static readonly FilePickerFileType VideoFileType = new("Video")
    {
        Patterns =
        [
            "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.webm", "*.avi", "*.mxf",
            "*.ts", "*.m2ts", "*.mts", "*.mpg", "*.mpeg", "*.wmv", "*.flv", "*.ogv", "*.3gp",
        ],
    };
    private static readonly FilePickerFileType AudioFileType = new("Audio")
    {
        Patterns = ["*.wav", "*.mp3", "*.aac", "*.m4a", "*.flac", "*.ac3", "*.opus", "*.ogg", "*.wma", "*.aif", "*.aiff"],
    };

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

    public MainWindow(PlaybackEngine? engine, Project? project, string status, string? projectPath = null,
        Proxy.ProxyService? proxy = null, Sprocket.Audio.AudioEngine? audioClock = null)
    {
        AvaloniaXamlLoader.Load(this);
        _engine = engine;
        _project = project;
        _proxy = proxy;
        _audioClock = audioClock;
        _currentProjectPath = projectPath;

        _root = this.FindControl<Control>("Root");
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _telemetryText = this.FindControl<TextBlock>("TelemetryText")!;
        _engineStateText = this.FindControl<TextBlock>("EngineStateText")!;
        _stateDot = this.FindControl<Ellipse>("StateDot")!;
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

        // Autosave (PLAN.md step 20): a debounced sidecar write driven off the dirty signal. Beside the project
        // file once it has one, else a per-user untitled slot — so a crash before the first manual save is still
        // recoverable. Independent of playback, so it runs even when no engine is available.
        if (_project is not null)
            _autosave = new AutosaveService(_project, _history, () => _currentProjectPath);

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
            {
                bool maximized = WindowState == WindowState.Maximized;
                _maxButton.Content = maximized ? "❐" : "▢";
                AutomationProperties.SetName(_maxButton, maximized ? "Restore" : "Maximize");
            }
            if (WindowState != WindowState.Minimized)
                _lastNonMinimizedState = WindowState; // remember the real state to persist (not a transient minimize)
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        WindowStateStore.Save(_lastNonMinimizedState); // remember maximized-or-not for next launch
        _telemetryTimer?.Stop(); // stop the status-bar poll (harmless if already idle)
        _statsOverlay?.Close(); // tear down the diagnostics overlay's poll timer
        _autosave?.Dispose(); // stop the autosave timer for this session
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
        this.FindControl<MenuItem>("OpenSampleMenuItem")!.Click += (_, _) => OpenSampleProject();
        this.FindControl<MenuItem>("SaveMenuItem")!.Click += (_, _) => Save();
        this.FindControl<MenuItem>("SaveAsMenuItem")!.Click += (_, _) => _ = SaveAsAsync();
        this.FindControl<MenuItem>("ImportMenuItem")!.Click += (_, _) => _ = ImportDialogAsync();
        this.FindControl<MenuItem>("RelinkMenuItem")!.Click += (_, _) => _ = RelinkMediaAsync();
        this.FindControl<MenuItem>("ExportMenuItem")!.Click += (_, _) => _ = ExportAsync();
        this.FindControl<MenuItem>("ExportQueueMenuItem")!.Click += (_, _) => OpenExportQueue();
        this.FindControl<MenuItem>("ExportEdlMenuItem")!.Click += (_, _) => _ = ExportInterchangeAsync(InterchangeKind.Edl);
        this.FindControl<MenuItem>("ExportFcpXmlMenuItem")!.Click += (_, _) => _ = ExportInterchangeAsync(InterchangeKind.FinalCutXml);
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
        _rippleDeleteMenuItem = this.FindControl<MenuItem>("RippleDeleteMenuItem")!;
        _cutMenuItem.Click += (_, _) => _timeline?.CutSelected();
        _copyMenuItem.Click += (_, _) => _timeline?.CopySelected();
        _pasteMenuItem.Click += (_, _) => _timeline?.PasteAtPlayhead();
        _deleteMenuItem.Click += (_, _) => _timeline?.DeleteSelected();
        _rippleDeleteMenuItem.Click += (_, _) => _timeline?.RippleDeleteSelected();
        this.FindControl<MenuItem>("EditMenu")!.SubmenuOpened += (_, _) => RefreshEditMenu();

        // ── Clip ──
        _unlinkMenuItem = this.FindControl<MenuItem>("ClipUnlinkMenuItem")!;
        _nudgeLeftMenuItem = this.FindControl<MenuItem>("NudgeLeftMenuItem")!;
        _nudgeRightMenuItem = this.FindControl<MenuItem>("NudgeRightMenuItem")!;
        _clipSpeedMenuItem = this.FindControl<MenuItem>("ClipSpeedMenuItem")!;
        _unlinkMenuItem.Click += (_, _) => _timeline?.UnlinkSelected();
        _nudgeLeftMenuItem.Click += (_, _) => _timeline?.NudgeSelected(-1);
        _nudgeRightMenuItem.Click += (_, _) => _timeline?.NudgeSelected(+1);
        _clipSpeedMenuItem.Click += async (_, _) => await ShowSpeedDialogAsync();
        _createMulticamMenuItem = this.FindControl<MenuItem>("CreateMulticamMenuItem")!;
        _createMulticamMenuItem.Click += (_, _) => CreateMulticamSource();
        _clipNormalizeMenuItem = this.FindControl<MenuItem>("ClipNormalizeMenuItem")!;
        _clipNormalizeMenuItem.Click += (_, _) => NormalizeSelectedClip();
        this.FindControl<MenuItem>("ClipMenu")!.SubmenuOpened += (_, _) => RefreshClipMenu();

        // ── Clip ▸ Insert (generators + adjustment layer, PLAN.md step 19) ──
        var insertItems = new System.Collections.Generic.List<MenuItem>();
        foreach (GeneratorDescriptor generator in GeneratorCatalog.BuiltIns)
        {
            GeneratorDescriptor g = generator; // capture per iteration
            var item = new MenuItem { Header = g.DisplayName };
            item.Click += (_, _) => _timeline?.InsertGenerator(g);
            insertItems.Add(item);
        }
        var adjustmentItem = new MenuItem { Header = "Adjustment Layer" };
        adjustmentItem.Click += (_, _) => _timeline?.InsertAdjustmentLayer();
        insertItems.Add(adjustmentItem);
        this.FindControl<MenuItem>("ClipInsertMenuItem")!.ItemsSource = insertItems;

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
        this.FindControl<MenuItem>("ZoomToFitMenuItem")!.Click += (_, _) => _timeline?.ZoomToFit();
        _snappingMenuItem = this.FindControl<MenuItem>("SnappingMenuItem")!;
        _guidesMenuItem = this.FindControl<MenuItem>("GuidesMenuItem")!;
        _showProjectMenuItem = this.FindControl<MenuItem>("ShowProjectMenuItem")!;
        _showInspectorMenuItem = this.FindControl<MenuItem>("ShowInspectorMenuItem")!;
        // Drive the toolbar toggle (the single source of truth) from the menu checkbox's new state.
        _snappingMenuItem.Click += (_, _) => { if (_snappingToggle is not null) _snappingToggle.IsChecked = _snappingMenuItem.IsChecked; };
        _guidesMenuItem.Click += (_, _) => { if (_guidesToggle is not null) _guidesToggle.IsChecked = _guidesMenuItem.IsChecked; };
        _showProjectMenuItem.Click += (_, _) => SetPanelVisible(project: true, _showProjectMenuItem.IsChecked == true);
        _showInspectorMenuItem.Click += (_, _) => SetPanelVisible(project: false, _showInspectorMenuItem.IsChecked == true);
        _showStatsMenuItem = this.FindControl<MenuItem>("ShowStatsMenuItem")!;
        _showStatsMenuItem.Click += (_, _) => ShowStatsOverlay(_showStatsMenuItem.IsChecked == true);
        this.FindControl<MenuItem>("ViewMenu")!.SubmenuOpened += (_, _) => RefreshViewMenu();

        // ── Sequence (multiple sequences + nested/compound clips, PLAN.md step 23) ──
        this.FindControl<MenuItem>("NewSequenceMenuItem")!.Click += (_, _) => NewSequence();
        this.FindControl<MenuItem>("SequenceSettingsMenuItem")!.Click += async (_, _) => await ShowSequenceSettingsAsync();
        _nestMenuItem = this.FindControl<MenuItem>("NestMenuItem")!;
        _nestMenuItem.Click += (_, _) => NestSelection();
        _openSequenceMenuItem = this.FindControl<MenuItem>("OpenSequenceMenuItem")!;
        this.FindControl<MenuItem>("SequenceMenu")!.SubmenuOpened += (_, _) => RefreshSequenceMenu();

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
        if (ctrl && shift && e.Key == Key.E) { OpenExportQueue(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.E) { _ = ExportAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.I) { _ = ImportDialogAsync(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Z) { _history.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { _history.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _history.Redo(); e.Handled = true; return; }
        // Jump to the previous marker (Premiere's Ctrl+Shift+M). Add (M) / next (Shift+M) are below the text guard.
        if (ctrl && shift && e.Key == Key.M) { JumpToMarker(-1); e.Handled = true; return; }
        // Timeline zoom (Resolve/FCP convention). Ctrl++/Ctrl+- are safe with a focused text field; the bare
        // Shift+Z "zoom to fit" is gated below the text-box guard. OemPlus/OemMinus are the main-row =/- keys;
        // Add/Subtract are the numpad equivalents.
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { _timeline?.ZoomIn(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { _timeline?.ZoomOut(); e.Handled = true; return; }

        // Below here are transport / editing keys that must not steal input from a focused text field
        // (the media-bin search box, the Inspector numeric boxes).
        if (IsTypingInTextBox())
            return;

        if (ctrl && e.Key == Key.X) { _timeline?.CutSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { _timeline?.CopySelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { _timeline?.PasteAtPlayhead(); e.Handled = true; }
        else if (shift && (e.Key == Key.Delete || e.Key == Key.Back)) { _timeline?.RippleDeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Delete || e.Key == Key.Back) { _timeline?.DeleteSelected(); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { _timeline?.NudgeSelected(-1); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { _timeline?.NudgeSelected(+1); e.Handled = true; }
        // Multicam angle switching (PLAN.md step 24): 1–9 cut the selected multicam clip to that angle at the
        // playhead — the Premiere/Resolve convention. Only swallow the digit when a multicam clip is selected.
        else if (!ctrl && !alt && TryAngleKey(e.Key, out int angle) && _timeline?.SelectedIsMulticam == true)
        {
            _timeline.SwitchSelectedAngle(angle);
            e.Handled = true;
        }
        // Jump-to-previous/next-keyframe of the selected clip (Premiere uses [ / ], step 16d).
        else if (e.Key == Key.OemOpenBrackets) { JumpToKeyframe(-1); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { JumpToKeyframe(+1); e.Handled = true; }
        // Markers (PLAN.md step 20): add at the playhead (M), jump to the next (Shift+M; previous is Ctrl+Shift+M).
        else if (shift && e.Key == Key.M) { JumpToMarker(+1); e.Handled = true; }
        else if (e.Key == Key.M) { AddMarker(); e.Handled = true; }
        else if (shift && e.Key == Key.Z) { _timeline?.ZoomToFit(); e.Handled = true; }
        else if (e.Key == Key.Space) { if (!_exporting) _active?.TogglePlayPause(); e.Handled = true; }
    }

    private bool IsTypingInTextBox() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>Maps a 1–9 number-row or numpad key to a zero-based multicam angle index (PLAN.md step 24).</summary>
    private static bool TryAngleKey(Key key, out int angle)
    {
        if (key >= Key.D1 && key <= Key.D9) { angle = key - Key.D1; return true; }
        if (key >= Key.NumPad1 && key <= Key.NumPad9) { angle = key - Key.NumPad1; return true; }
        angle = 0;
        return false;
    }

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

    /// <summary>Adds a sequence marker at the playhead (PLAN.md step 20) and reports it on the status strip.</summary>
    private void AddMarker()
    {
        if (_timeline?.AddMarkerAtPlayhead() is { } marker)
            SetStatus($"Marker added at {FormatTime(marker.Time)}");
    }

    /// <summary>
    /// Seeks the Program playhead to the previous (<paramref name="direction"/> &lt; 0) or next sequence marker
    /// (PLAN.md step 20), mirroring the keyframe navigation. A no-op when there is no marker in that direction.
    /// </summary>
    private void JumpToMarker(int direction)
    {
        if (_project is null || _program is null)
            return;
        Timecode now = _program.Position;
        Marker? target = direction < 0
            ? MarkerNavigation.Previous(_project.Timeline.Markers, now)
            : MarkerNavigation.Next(_project.Timeline.Markers, now);
        if (target is { } m)
        {
            _program.SeekTo(m.Time);
            SetStatus(MarkerListFormat.Describe(m, _project.Timeline.Markers.IndexOf(m)));
        }
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
        UpdateSequenceBadge();
        UpdateTelemetry();
        UpdateTimelineHeader();
    }

    private void UpdateProjectTitle() => this.FindControl<TextBlock>("ProjectTitleText")!.Text = _projectName;

    /// <summary>The sequence badge: the active sequence's name and render format (PLAN.md step 23). With one
    /// sequence it reads like the pre-step-23 badge plus the name.</summary>
    private void UpdateSequenceBadge()
    {
        if (_project is null)
            return;
        Timeline timeline = _project.Timeline;
        double fps = Fps(timeline.FrameRate);
        (int w, int h) = (timeline.Resolution.Width, timeline.Resolution.Height);
        string resLabel = h switch { 2160 => "4K", 1080 => "1080p", 720 => "720p", _ => $"{w}×{h}" };
        this.FindControl<TextBlock>("SequenceBadge")!.Text = $"{_project.ActiveSequence.Name} · {resLabel} · {fps:0.##}";
    }

    /// <summary>The status-bar telemetry at rest: the sequence's <em>nominal</em> rate · resolution · duration
    /// (UI.md §3.7). Called on session/sequence changes and when playback stops; during playback the timer feeds
    /// <see cref="RenderTelemetry"/> the measured rate instead.</summary>
    private void UpdateTelemetry()
    {
        if (_project is null)
            return;
        RenderTelemetry(Fps(_project.Timeline.FrameRate));
    }

    /// <summary>Renders the right-hand telemetry cell as <c>fps · WxH · duration</c> for a given frame rate (nominal
    /// at rest, measured while playing). Only assigns when the string changes, so a steady readout never re-lays-out
    /// the status bar. No framework/runtime text (UI.md §3.7).</summary>
    private void RenderTelemetry(double fps)
    {
        if (_project is null)
            return;
        Timeline timeline = _project.Timeline;
        (int w, int h) = (timeline.Resolution.Width, timeline.Resolution.Height);
        Timecode duration = _engine?.Duration ?? timeline.Duration;
        string text = StatusBarFormat.Telemetry(fps, w, h, FormatTime(duration));
        if (_telemetryText!.Text != text)
            _telemetryText.Text = text;
    }

    /// <summary>
    /// Updates the left status group — the state dot + <c>State · GPU/CPU · device</c> (UI.md §3.7). The GPU /
    /// hardware-accel status is the top-most decoding layer at the playhead; nothing decoding (a gap / generator)
    /// shows just the state word. Reads a cached managed snapshot (<see cref="PlaybackEngine.GetActiveVideoDecodeInfo"/>),
    /// never native decoder state, so it is cheap and UI-thread-safe. Assigns only on change to avoid needless layout.
    /// </summary>
    private void UpdateEngineStatus()
    {
        PlaybackEngine? engine = _active?.CurrentEngine ?? _engine;
        PlaybackState state = _active?.State ?? engine?.State ?? PlaybackState.Stopped;
        bool playing = state == PlaybackState.Playing;
        Media.VideoDecodeInfo? decode = engine?.GetActiveVideoDecodeInfo();

        string label = StatusBarFormat.EngineLabel(state, decode);

        // Idle/paused = neutral dot; playing = green, or amber on the software (CPU) path — the usual 1080p
        // stutter cause, worth flagging the same way the Playback Statistics overlay does.
        IBrush dot = !playing ? Palette.MutedTextBrush
            : decode is { IsHardwareAccelerated: false } ? Palette.WarnBrush
            : Palette.GoodBrush;

        if (_engineStateText!.Text != label)
            _engineStateText.Text = label;
        if (_stateDot is not null && !ReferenceEquals(_stateDot.Fill, dot))
            _stateDot.Fill = dot;
    }

    /// <summary>
    /// Starts the 1 Hz live-telemetry poll (created lazily) and seeds the fps baseline. Called on transition to
    /// Playing. The timer runs only while playing (see <see cref="StopTelemetryTimer"/>), so an idle editor does no
    /// periodic work — the readout is event-driven at rest (ARCHITECTURE.md §1: no work on the frame hot path).
    /// </summary>
    private void StartTelemetryTimer()
    {
        PlaybackEngine? engine = _active?.CurrentEngine ?? _engine;
        _prevStatsTs = Stopwatch.GetTimestamp();
        _prevPresented = engine?.GetStatistics().FramesPresented ?? 0;

        if (_telemetryTimer is null)
        {
            // 1 Hz: fps is glanceable, and a 1 s window keeps frame-count quantization to ≈±1 fps while halving the
            // wake-ups of the diagnostics overlay's 2 Hz poll. It only ever runs during playback.
            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _telemetryTimer.Tick += OnTelemetryTick;
        }
        _telemetryTimer.Start();
    }

    /// <summary>Stops the live poll and settles the status bar back to the nominal-rate readout + idle state dot.</summary>
    private void StopTelemetryTimer()
    {
        _telemetryTimer?.Stop();
        UpdateTelemetry();
        UpdateEngineStatus();
    }

    /// <summary>The live-telemetry tick: derives the measured preview fps from the delta of the engine's cumulative
    /// present counter over the real elapsed interval, and refreshes the GPU/hw-accel status (it can change as the
    /// playhead crosses clips). Stops itself if playback has ended so it never spins at idle.</summary>
    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        PlaybackEngine? engine = _active?.CurrentEngine ?? _engine;
        if (engine is null || (_active?.State ?? PlaybackState.Stopped) != PlaybackState.Playing)
        {
            StopTelemetryTimer();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        PlaybackStatistics stats = engine.GetStatistics();
        double seconds = (now - _prevStatsTs) / (double)Stopwatch.Frequency;
        if (seconds > 0)
            RenderTelemetry((stats.FramesPresented - _prevPresented) / seconds);
        _prevPresented = stats.FramesPresented;
        _prevStatsTs = now;

        UpdateEngineStatus();
    }

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
        // Double-clicking a transition in the browser applies it to the selected clip's cut (PLAN.md step 25).
        browser.TransitionActivated += id => _timeline?.ApplyTransitionToSelectedCut(id);
        browser.Attach(_project, _history, _thumbnails);

        WireMixer(browser);
    }

    /// <summary>
    /// Installs the audio mixer into the Project panel's Audio tab (PLAN.md step 30, UI.md §3.3): per-track gain /
    /// pan / mute / solo + a master strip with the live loudness meters, and loudness-normalization to a target at
    /// track / master scope. The meters read the audio engine's live loudness (null on the software clock);
    /// normalization measures a scope's raw loudness offline through the same decode plumbing.
    /// </summary>
    private void WireMixer(MediaBrowserPanel browser)
    {
        if (_project is null)
            return;

        _mixer = new Mixer.MixerView();
        Func<Sprocket.Audio.Loudness.LoudnessSnapshot>? readLoudness =
            _audioClock is { } clock ? () => clock.CurrentLoudness : null;

        _mixer.Attach(
            _project, _history,
            readLoudness,
            measureTrack: track => MeasureTrackLoudness(track),
            measureMaster: () => MeasureMasterLoudness());
        browser.SetMixer(_mixer);
    }

    /// <summary>Measures one audio track's raw integrated loudness (unity track/master gain) for normalization
    /// (PLAN.md step 30). Runs on the caller (UI) thread over the project's audio span; sources decode through the
    /// same PCM readers playback uses. Offline sources measure as silence.</summary>
    private Sprocket.Audio.Loudness.LoudnessMeasurement MeasureTrackLoudness(AudioTrack track)
    {
        if (_project is null)
            return Sprocket.Audio.Loudness.LoudnessMeasurement.Silent;
        using AudioMixer mixer = MediaBootstrap.CreateAnalysisMixer(_project);
        var scope = new AudioPlanScope(OnlyTrack: track, UnityTrackGain: true, UnityMasterGain: true);
        return Sprocket.Audio.Loudness.LoudnessAnalyzer.MeasureMix(
            mixer, _project, _project.ActiveSequence, Timecode.Zero, _project.ActiveSequence.Timeline.Duration, scope);
    }

    /// <summary>Measures the full mix's integrated loudness at unity master gain for master normalization
    /// (PLAN.md step 30). Offline sources measure as silence.</summary>
    private Sprocket.Audio.Loudness.LoudnessMeasurement MeasureMasterLoudness()
    {
        if (_project is null)
            return Sprocket.Audio.Loudness.LoudnessMeasurement.Silent;
        using AudioMixer mixer = MediaBootstrap.CreateAnalysisMixer(_project);
        return Sprocket.Audio.Loudness.LoudnessAnalyzer.MeasureMix(
            mixer, _project, _project.ActiveSequence, Timecode.Zero, _project.ActiveSequence.Timeline.Duration,
            new AudioPlanScope(UnityMasterGain: true));
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
        WireMarkersButton();

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

        // Show the decode path (GPU/CPU) in the status bar as soon as frame 0 has decoded — without any idle
        // polling. A one-shot present handler refreshes the status once, then unsubscribes; at startup the decode
        // info isn't populated yet (the first pump is still in flight), so the initial RefreshTransportForActive
        // above only had the state word. FramePresented fires on the pump thread, so marshal to the UI thread.
        void OnFirstPresent()
        {
            _engine!.FramePresented -= OnFirstPresent;
            Dispatcher.UIThread.Post(UpdateEngineStatus);
        }
        _engine!.FramePresented += OnFirstPresent;

        // A pump iteration can fault (e.g. the audio device hiccupping during the end-of-timeline stop); the
        // engine keeps the transport alive rather than dying, so surface the reason instead of swallowing it.
        _engine!.PumpError += ex => Dispatcher.UIThread.Post(() => SetStatus($"Playback recovered from an error: {ex.Message}"));

        // Preview proxies (PLAN.md step 18): the engine already switches onto a proxy transparently when one is
        // ready (wired in the bootstrap); here we just reflect progress in the status bar without interrupting flow.
        if (_proxy is { Enabled: true })
        {
            _proxy.ProxyReady += _ => Dispatcher.UIThread.Post(() =>
            {
                if (_proxy.StatusSummary() is { } summary)
                    SetStatus(summary);
            });
        }

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
            SetPlayPauseGlyph(state == PlaybackState.Playing);
            UpdateEngineStatus();
            if (state == PlaybackState.Playing)
                StartTelemetryTimer();
            else
                StopTelemetryTimer(); // Paused / Stopped → settle to the nominal readout, stop polling
        });
    }

    /// <summary>
    /// Keeps the Play/Pause button's glyph and its screen-reader name in sync: while playing it shows the
    /// pause glyph and announces "Pause"; while paused it shows the play glyph and announces "Play".
    /// </summary>
    private void SetPlayPauseGlyph(bool playing)
    {
        if (_playPause is null)
            return;
        _playPause.Content = playing ? "❚❚" : "▶";
        AutomationProperties.SetName(_playPause, playing ? "Pause" : "Play");
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
        SetPlayPauseGlyph(m.State == PlaybackState.Playing);
        UpdateEngineStatus();
        // Follow the newly-active monitor's transport: poll while it's playing, otherwise stay event-driven.
        if (m.State == PlaybackState.Playing)
            StartTelemetryTimer();
        else
            StopTelemetryTimer();
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
        timeline.Status += SetStatus;                 // transition hints, etc. (PLAN.md step 25)
        WireTrackRename(timeline);

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
        WireTool("RippleTool", EditTool.Ripple, timeline);
        WireTool("RollTool", EditTool.Roll, timeline);
        WireTool("SlipTool", EditTool.Slip, timeline);
        WireTool("SlideTool", EditTool.Slide, timeline);
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

    /// <summary>
    /// Wires the inline track-rename editor: the timeline raises <see cref="TimelineControl.TrackRenameRequested"/>
    /// on a name double-click; we position the overlaid <c>TrackRenameEditor</c> over the name and focus it.
    /// Enter / lost-focus commit through the edit history (undoable); Escape cancels.
    /// </summary>
    private void WireTrackRename(TimelineControl timeline)
    {
        _trackRenameEditor = this.FindControl<TextBox>("TrackRenameEditor")!;

        timeline.TrackRenameRequested += (track, rect) =>
        {
            _renameTarget = track;
            _trackRenameEditor.Margin = new Thickness(rect.X, rect.Y, 0, 0);
            _trackRenameEditor.Width = rect.Width;
            _trackRenameEditor.Height = rect.Height;
            _trackRenameEditor.Text = track.Name;
            _trackRenameEditor.IsVisible = true;
            // Focus after layout so the freshly-shown box takes focus and selects its text.
            Dispatcher.UIThread.Post(() =>
            {
                _trackRenameEditor.Focus();
                _trackRenameEditor.SelectAll();
            });
        };

        _trackRenameEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                CommitTrackRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTrackRename();
                e.Handled = true;
            }
        };
        _trackRenameEditor.LostFocus += (_, _) => CommitTrackRename();
    }

    // Commits the inline rename (no-op if the editor is hidden / nothing targeted). Clearing the target and
    // hiding before delegating means the LostFocus that hiding triggers re-enters as a no-op.
    private void CommitTrackRename()
    {
        if (_renameTarget is null || _trackRenameEditor is null || !_trackRenameEditor.IsVisible)
            return;
        Sprocket.Core.Model.Track target = _renameTarget;
        string text = _trackRenameEditor.Text ?? string.Empty;
        _renameTarget = null;
        _trackRenameEditor.IsVisible = false;
        _timeline?.CommitTrackRename(target, text);
    }

    private void CancelTrackRename()
    {
        _renameTarget = null;
        if (_trackRenameEditor is not null)
            _trackRenameEditor.IsVisible = false;
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

    // ── Markers panel (PLAN.md step 20) ─────────────────────────────────────────────────────────────

    /// <summary>Attaches a flyout to the <c>Markers</c> header button: the markers panel — an "add at playhead"
    /// action plus one row per sequence marker (click to seek, ✕ to remove). Rebuilt each time it opens so it
    /// reflects the current marker list.</summary>
    private void WireMarkersButton()
    {
        var button = this.FindControl<Button>("MarkersButton");
        if (button is null)
            return;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opened += (_, _) => flyout.Content = BuildMarkersPanel();
        button.Flyout = flyout;
    }

    /// <summary>Builds the markers-panel content (PLAN.md step 20, UI.md §3.6): an add-at-playhead button and a
    /// scrollable list of the sequence markers, each seeking on click with a remove (✕) button.</summary>
    private Control BuildMarkersPanel()
    {
        var root = new StackPanel { Spacing = 6, MinWidth = 260, MaxWidth = 320, Margin = new Thickness(4) };
        root.Children.Add(new TextBlock
        {
            Text = "Markers", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 12,
        });

        var addButton = new Button { Content = "+ Add at playhead", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        addButton.Click += (_, _) => { AddMarker(); if (this.FindControl<Button>("MarkersButton")?.Flyout is Flyout f) f.Content = BuildMarkersPanel(); };
        root.Children.Add(addButton);

        var markers = _project?.Timeline.Markers ?? [];
        if (markers.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "No markers yet. Press M to add one at the playhead.",
                FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            return root;
        }

        var list = new StackPanel { Spacing = 2 };
        // Show markers in time order without mutating the model list.
        var ordered = markers.Select((m, i) => (Marker: m, Index: i)).OrderBy(x => x.Marker.Time.Ticks).ToList();
        foreach ((Marker marker, int index) in ordered)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("8,*,Auto") };

            row.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                Background = TimelineControl.MarkerBrush(marker.Color),
            });

            var seek = new Button
            {
                Content = MarkerListFormat.Describe(marker, index),
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Transparent,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Marker captured = marker;
            seek.Click += (_, _) => { _program?.SeekTo(captured.Time); };
            Grid.SetColumn(seek, 1);
            row.Children.Add(seek);

            var remove = new Button { Content = "✕", FontSize = 11, Padding = new Thickness(6, 2) };
            ToolTip.SetTip(remove, "Remove marker");
            remove.Click += (_, _) => { _timeline?.RemoveMarker(captured); if (this.FindControl<Button>("MarkersButton")?.Flyout is Flyout f) f.Content = BuildMarkersPanel(); };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            list.Children.Add(row);
        }

        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 280, Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        return root;
    }

    // ── Context-enabling: refresh menu items on submenu open ────────────────────────────────────────

    private void RefreshEditMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        if (_cutMenuItem is not null) _cutMenuItem.IsEnabled = sel;
        if (_copyMenuItem is not null) _copyMenuItem.IsEnabled = sel;
        if (_pasteMenuItem is not null) _pasteMenuItem.IsEnabled = _timeline?.CanPaste == true;
        if (_deleteMenuItem is not null) _deleteMenuItem.IsEnabled = sel;
        if (_rippleDeleteMenuItem is not null) _rippleDeleteMenuItem.IsEnabled = sel;
    }

    private void RefreshClipMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        if (_unlinkMenuItem is not null) _unlinkMenuItem.IsEnabled = _timeline?.SelectedIsLinked == true;
        if (_nudgeLeftMenuItem is not null) _nudgeLeftMenuItem.IsEnabled = sel;
        if (_nudgeRightMenuItem is not null) _nudgeRightMenuItem.IsEnabled = sel;
        if (_clipSpeedMenuItem is not null) _clipSpeedMenuItem.IsEnabled = sel;
        if (_createMulticamMenuItem is not null) _createMulticamMenuItem.IsEnabled = _timeline?.CanCreateMulticam == true;
        if (_clipNormalizeMenuItem is not null) _clipNormalizeMenuItem.IsEnabled = SelectedClipHasAudio();
    }

    /// <summary>Whether the timeline selection is a clip whose source carries audio (so Clip ▸ Normalize Audio can act).</summary>
    private bool SelectedClipHasAudio() =>
        _project is not null && _selectedClip is { } clip && clip.Kind == ClipKind.Media
        && _project.MediaPool.Get(clip.MediaRefId) is { Info.HasAudio: true };

    /// <summary>
    /// Clip ▸ Normalize Audio (PLAN.md step 30): measures the selected clip's raw loudness over its used source span
    /// and sets its <see cref="Clip.GainDb"/> so it hits the mixer's target (true-peak limited), as one undoable
    /// edit. Applying it to several clips in turn matches their loudness (the gain-match pass).
    /// </summary>
    private void NormalizeSelectedClip()
    {
        if (_project is null || _selectedClip is not { } clip || !SelectedClipHasAudio())
            return;

        using IPcmReader? reader = MediaBootstrap.OpenPcmReaderFor(_project, clip.MediaRefId);
        if (reader is null)
        {
            SetStatus("Cannot normalize: the clip's audio could not be opened.");
            return;
        }

        Sprocket.Audio.Loudness.LoudnessMeasurement m = Sprocket.Audio.Loudness.LoudnessAnalyzer.MeasureSource(
            reader, clip.SourceIn, clip.SourceOut - clip.SourceIn);
        if (double.IsNegativeInfinity(m.IntegratedLufs))
        {
            SetStatus("Clip is silent — nothing to normalize.");
            return;
        }

        double target = _mixer?.TargetLufs ?? LoudnessNormalization.StreamingMinus14Lufs;
        double gain = LoudnessNormalization.ComputeGainDb(m.IntegratedLufs, m.TruePeakDbtp, target);
        _history.Execute(SetPropertyCommand<double>.Create(
            "Normalize clip audio", () => clip.GainDb, v => clip.GainDb = v, gain));
        SetStatus($"Normalized clip to {target:0.#} LUFS ({MixerFormat.GainDbLabel(gain)}).");
    }

    private void RefreshEffectsMenu()
    {
        bool sel = _timeline?.HasSelection == true;
        foreach (MenuItem item in _effectsMenuItems)
            item.IsEnabled = sel;
    }

    /// <summary>Clip ▸ Speed / Duration (PLAN.md step 21): prompts for a speed percentage and retimes the
    /// selected clip (and its linked companions) through the command stack.</summary>
    private async Task ShowSpeedDialogAsync()
    {
        if (_timeline is not { HasSelection: true } timeline)
            return;
        Sprocket.Core.Timing.Rational? speed = await SpeedDialog.Show(this, timeline.SelectedClipSpeed);
        if (speed is { } s)
        {
            timeline.SetSelectedClipSpeed(s);
            SetStatus($"Clip speed set to {(s.ToDouble() * 100):0.##}%.");
        }
    }

    private void RefreshViewMenu()
    {
        if (_snappingMenuItem is not null) _snappingMenuItem.IsChecked = _snappingToggle?.IsChecked == true;
        if (_guidesMenuItem is not null) _guidesMenuItem.IsChecked = _guidesToggle?.IsChecked == true;
        if (_showProjectMenuItem is not null) _showProjectMenuItem.IsChecked = _projectPane?.IsVisible != false;
        if (_showInspectorMenuItem is not null) _showInspectorMenuItem.IsChecked = _inspectorPane?.IsVisible != false;
        if (_showStatsMenuItem is not null) _showStatsMenuItem.IsChecked = _statsOverlay is not null;
    }

    /// <summary>
    /// View ▸ Playback Statistics: opens or closes the floating diagnostics overlay (effective vs. target frame
    /// rate, dropped frames, CPU / memory / GC). The overlay polls whichever monitor's engine is active, so it
    /// reflects the Program timeline or a Source preview as the user switches tabs. Non-modal and always-on-top.
    /// </summary>
    private void ShowStatsOverlay(bool show)
    {
        if (show)
        {
            if (_statsOverlay is not null)
                return;
            var overlay = new PlaybackStatsOverlay(() => _active?.CurrentEngine ?? _engine);
            overlay.Closed += (_, _) =>
            {
                _statsOverlay = null;
                if (_showStatsMenuItem is not null)
                    _showStatsMenuItem.IsChecked = false;
            };
            _statsOverlay = overlay;
            overlay.Show(this);   // non-modal child window
            overlay.PlaceNear(this);
        }
        else
        {
            _statsOverlay?.Close(); // Closed handler clears the field + unchecks the menu item
        }
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
        // Undoing a sequence-add (or a removal's redo) can strip the sequence that is currently open. Switching the
        // active sequence is navigation, not part of the command (ARCHITECTURE.md §17), so heal it here: fall back
        // to a surviving sequence so the editor never points at one no longer in the project (PLAN.md step 23).
        if (_project is { } project && !project.Sequences.Contains(project.ActiveSequence) && project.Sequences.Count > 0)
            SwitchToSequence(project.Sequences[^1]);

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

        // Refresh the monitor for edits that only change how the current frame composites — a track's visibility
        // (the eye toggle), an effect parameter (a Color/Brightness slider drag), opacity/blend. While playing the
        // pump repaints every frame, but while paused it only presents on a decode or a seek, so these edits would
        // otherwise not show until the next scrub/play. The surface recomposites from live model state on each draw
        // (it honours track.Enabled and resolves each clip's effects at the playhead) over the already-held native
        // frames, so a repaint alone reflects the edit with no re-decode. (Null until the transport is wired.)
        _preview?.InvalidateVisual();
    }

    // ── Sequences: multiple sequences + nested/compound clips (PLAN.md step 23) ─────────────────────

    /// <summary>Sequence ▸ New Sequence: creates a fresh empty sequence (one video + one audio track, the active
    /// sequence's render format) through the command stack and opens it. Mirrors Premiere's File ▸ New ▸ Sequence,
    /// which makes the new sequence the active one.</summary>
    private void NewSequence()
    {
        if (_project is null)
            return;

        Timeline current = _project.Timeline;
        var timeline = new Timeline(current.FrameRate, current.Resolution, current.SampleRate);
        timeline.Tracks.Add(new VideoTrack { Name = "V1" });
        timeline.Tracks.Add(new AudioTrack { Name = "A1" });
        var sequence = new Sequence(SequenceId.New(), SequenceNaming.NextUnique(_project, "Sequence"), timeline);

        _history.Execute(new AddSequenceCommand(_project, sequence));
        SwitchToSequence(sequence);
        SetStatus($"New sequence: {sequence.Name}");
    }

    /// <summary>Sequence ▸ Nest: nests the timeline selection (with its linked companions) into a new child
    /// sequence (PLAN.md step 23). The heavy lifting is the tested <see cref="Core.Model.SequenceNesting"/>; this
    /// just routes it and reports. A no-op when nothing is selected.</summary>
    private void NestSelection()
    {
        if (_timeline?.NestSelection() is { } child)
            SetStatus($"Nested selection into {child.Name}");
    }

    /// <summary>Clip ▸ Create Multicam Source: collapses the stacked video angles into one synced multicam clip
    /// (PLAN.md step 24). The work is the tested <see cref="Core.Model.MulticamBuilder"/>; this routes it and
    /// reports. Switch angles afterwards with the 1–9 keys or the Inspector.</summary>
    private void CreateMulticamSource()
    {
        if (_timeline?.CreateMulticamSource() is { } name)
            SetStatus($"Created {name} — press 1–9 to switch angle at the playhead");
    }

    /// <summary>
    /// Opens a sequence in the timeline in place (PLAN.md step 23): re-points the model's active sequence, re-points
    /// the Program monitor + preview at its (possibly different) resolution, and rewinds so the engine's pump
    /// reconciles its per-track players onto the new sequence's tracks. The edit history is shared across sequences,
    /// so undo/redo keeps working after a switch. <paramref name="sequence"/> must be one of the project's sequences.
    /// </summary>
    private void SwitchToSequence(Sequence sequence)
    {
        if (_project is null || ReferenceEquals(_project.ActiveSequence, sequence))
            return;

        _project.ActiveSequence = sequence; // throws if not a member; callers only pass project sequences

        Resolution res = sequence.Timeline.Resolution;
        _program?.SetFrameSize(res.Width, res.Height);
        _engine?.SeekTo(Timecode.Zero); // pump reconciles players to the new tracks and presents frame 0
        if (_active is not null && ReferenceEquals(_active, _program))
        {
            BindActiveToSurface();
            RefreshTransportForActive();
        }

        _timeline?.OnActiveSequenceChanged(); // drop the (old-sequence) selection + repaint on the new timeline
        UpdateSequenceBadge();
        UpdateTelemetry();
        UpdateTimelineHeader();
    }

    /// <summary>On Sequence-menu open: enables Nest only with a selection, and (re)builds the Open Sequence
    /// submenu listing every sequence with the active one checked (PLAN.md step 23).</summary>
    private void RefreshSequenceMenu()
    {
        if (_nestMenuItem is not null)
            _nestMenuItem.IsEnabled = _timeline?.HasSelection == true;

        if (_openSequenceMenuItem is null || _project is null)
            return;

        var items = new List<MenuItem>(_project.Sequences.Count);
        foreach (Sequence seq in _project.Sequences)
        {
            Sequence captured = seq; // capture per iteration
            var item = new MenuItem
            {
                Header = seq.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ReferenceEquals(seq, _project.ActiveSequence),
            };
            item.Click += (_, _) => SwitchToSequence(captured);
            items.Add(item);
        }
        _openSequenceMenuItem.ItemsSource = items;
        _openSequenceMenuItem.IsEnabled = items.Count > 0;
    }

    /// <summary>Sequence ▸ Settings: shows the active sequence's render format and lets the user rename it
    /// (undoable). The format is fixed after creation in this build (a format change would re-scale every clip's
    /// geometry), so it is shown read-only — matching how most editors gate sequence-settings changes.</summary>
    private async Task ShowSequenceSettingsAsync()
    {
        if (_project is null)
            return;

        Sequence active = _project.ActiveSequence;
        if (await SequenceSettingsDialog.Show(this, active) is not { } newName || newName == active.Name)
            return;

        _history.Execute(SetPropertyCommand<string>.Create(
            "Rename sequence", () => active.Name, v => active.Name = v, newName));
        UpdateSequenceBadge();
        SetStatus($"Renamed sequence to {newName}");
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
                    Patterns = [.. VideoFileType.Patterns!, .. AudioFileType.Patterns!],
                },
                VideoFileType,
                AudioFileType,
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
        if (added > 0)
            _proxy?.Enqueue(_project); // queue proxies for any newly-imported heavy sources (PLAN.md step 18)

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
        if (BlockedByExport())
            return;
        if (!await ConfirmDiscardIfDirty())
            return;

        var project = new Project();
        project.Timeline.Tracks.Add(new VideoTrack { Name = "V1" });
        project.Timeline.Tracks.Add(new AudioTrack { Name = "A1" });
        SessionRequested?.Invoke(new SessionRequest(project, "New project", null));
    }

    /// <summary>
    /// File ▸ Open Sample Project: after confirming any unsaved changes, builds a project over the demo clip
    /// bundled next to the executable and requests a session over it (PLAN.md step 16c). The sample opens as an
    /// untitled project — Save / Save As writes a fresh project file rather than touching the bundled clip. A
    /// no-op with a status hint when the clip isn't present (e.g. a build that didn't copy the asset).
    /// </summary>
    private async void OpenSampleProject()
    {
        if (BlockedByExport())
            return;
        if (!await ConfirmDiscardIfDirty())
            return;

        try
        {
            // Prefer the checked-in curated project (a graded timeline loaded through the normal ProjectSerializer
            // path); its project-relative "sample.mp4" resolves against the bundled Samples folder. Fall back to
            // building a plain project over the clip if only the clip shipped (or the project file is unreadable).
            Project? project = LoadBundledSampleProject();
            if (project is null && MediaBootstrap.SampleMediaPath() is { } clip)
                project = MediaBootstrap.BuildProjectFromMedia(clip);
            if (project is null)
            {
                SetStatus("Sample project is not available in this build.");
                return;
            }
            SessionRequested?.Invoke(new SessionRequest(project, "Opened sample project", null));
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the sample project: {ex.Message}");
        }
    }

    /// <summary>Loads the bundled sample project file, or <see langword="null"/> when it is absent or unreadable
    /// (so the caller can fall back to a plain clip-only project). It opens as untitled — Save / Save As writes a
    /// fresh file rather than touching the bundled asset.</summary>
    private static Project? LoadBundledSampleProject()
    {
        if (MediaBootstrap.SampleProjectPath() is not { } projectPath)
            return null;
        try
        {
            return ProjectSerializer.Load(projectPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>File ▸ Open: after confirming unsaved changes, loads a project JSON and requests a session over
    /// it. Load is offline-tolerant (§15); a parse/schema error is surfaced rather than thrown at the user.</summary>
    private async Task OpenProjectAsync()
    {
        if (BlockedByExport())
            return;
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
            bool recover = await ShouldRecoverAsync(path);
            Project project;
            string status;
            if (recover)
            {
                // Load the newer autosave instead, resolving relative media against the project's own directory
                // (the sidecar was written with the project path, so its relative paths match).
                string autosavePath = Autosave.SidecarPath(path);
                project = ProjectSerializer.Deserialize(
                    File.ReadAllText(autosavePath), Path.GetDirectoryName(Path.GetFullPath(path)));
                status = $"Recovered {Path.GetFileName(path)} from autosave";
            }
            else
            {
                project = ProjectSerializer.Load(path);
                status = $"Opened {Path.GetFileName(path)}";
            }
            SessionRequested?.Invoke(new SessionRequest(project, status, path));
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Crash recovery (PLAN.md step 20): if a newer autosave sidecar sits beside the project being opened, ask
    /// whether to recover it. The decision is the pure <see cref="AutosaveRecovery"/>; this just gathers the
    /// filesystem timestamps and shows the prompt. Returns <c>true</c> to load the autosave instead.
    /// </summary>
    private async Task<bool> ShouldRecoverAsync(string projectPath)
    {
        string autosavePath = Autosave.SidecarPath(projectPath);
        var state = new AutosaveRecovery.State(
            File.Exists(autosavePath),
            File.Exists(autosavePath) ? File.GetLastWriteTimeUtc(autosavePath) : default,
            File.Exists(projectPath),
            File.Exists(projectPath) ? File.GetLastWriteTimeUtc(projectPath) : default);
        if (!AutosaveRecovery.ShouldOffer(state))
            return false;

        return await ConfirmDialog.Show(
            this, "Recover unsaved changes?",
            "A more recent autosave was found for this project — it may contain changes that weren't saved before "
            + "the app last closed. Recover it, or open the last saved version?",
            "Recover", "Open saved version");
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
            // A clean save makes the autosave stale: clear the dirty flag and drop the sidecar so launch won't
            // offer to recover an older copy (PLAN.md step 20).
            _autosave?.ClearDirty();
            Autosave.Delete(Autosave.SidecarPath(path));
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
    /// Exports the loaded project to an <c>.mp4</c> the user picks via a Save dialog, on a background thread
    /// (export is CPU-bound and must not block the UI). A modal <see cref="ExportProgressDialog"/> shows
    /// determinate progress and offers Cancel; on completion the user can open the containing folder. Pausing
    /// playback first is mandatory — a second concurrent libav* pipeline crashes the in-process muxer. The
    /// actual pipeline lives in <see cref="VideoExporter"/>; this is just the composition-root trigger.
    /// </summary>
    private async Task ExportAsync()
    {
        if (_exporting || _project is null)
            return;

        // Nothing to render? Say so, rather than opening a Save dialog that would only fail at encode time.
        if (_project.Timeline.Duration <= Timecode.Zero)
        {
            await MessageDialog.Show(this, "Nothing to Export",
                "The timeline is empty — add a clip before exporting.");
            return;
        }

        // Choose the delivery container / codecs / quality (PLAN.md step 27 matrix) before picking the file.
        Resolution res = _project.Timeline.Resolution;
        if (await ExportSettingsDialog.Show(this, res.Width, res.Height) is not { } options)
            return; // user cancelled the settings dialog

        // Let the user choose where the file goes (mirrors File ▸ Save As) instead of silently dropping a fixed
        // file into the app's own (often read-only) install folder, where it would go unnoticed. The extension +
        // file-type filter follow the chosen container.
        ExportFormat format = options.Format;
        string extension = format.FileExtension; // ".mp4", ".mkv", …
        var fileType = new FilePickerFileType($"{ExportCodecs.Container(format.Container).DisplayName} video")
        {
            Patterns = ["*" + extension],
            MimeTypes = [format.MimeType],
        };
        string baseName = _currentProjectPath is null ? _projectName : ProjectDisplayName(_currentProjectPath);
        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Video",
            SuggestedFileName = baseName + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [fileType],
        });
        if (target?.TryGetLocalPath() is not { } outputPath)
            return; // user cancelled the picker — nothing exported, nothing to clean up

        _exporting = true;
        SetEnabled(false); // gate transport + tab-switching: no new in-process decode pipeline may start mid-export

        // The export runs an in-process FFmpeg muxer; a second concurrent libav* pipeline crashes it with a native
        // access violation (ProxyTranscoder documents the same hazard, which is why it shells out). So quiesce
        // every in-process decode pipeline first — Pause() is not enough (the pump + decode-ring workers keep
        // running). Suspend the Program engine and tear down the Source monitor's decoder if its tab is open.
        bool sourceWasActive = ReferenceEquals(_active, _source);
        _source?.Deactivate();
        if (_engine is not null)
            await _engine.SuspendAsync();

        using var cts = new CancellationTokenSource();
        var dialog = new ExportProgressDialog(Path.GetFileName(outputPath), cts);
        var progress = new Progress<double>(p => dialog.SetProgress(p));
        _ = dialog.ShowDialog(this); // modal: input-blocks the shell while exporting; dismissed in the finally

        bool ok = false;
        bool cancelled = false;
        string? error = null;
        try
        {
            await Task.Run(() => VideoExporter.Export(
                _project, outputPath, options, progress, cts.Token));
            ok = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // VideoExporter deletes the partial (unfinalized) file on cancel
        }
        catch (Exception ex)
        {
            error = ex.Message; // ditto on failure — no corrupt .mp4 is left behind
        }
        finally
        {
            dialog.CompleteAndClose();
            _engine?.Resume();      // restart the Program pump (feeds rebuild + re-present the current frame)
            if (sourceWasActive)
                _source?.Activate(); // reopen the Source monitor's decoder if it was showing
            _exporting = false;
            SetEnabled(true);
        }

        if (ok)
        {
            SetStatus($"Exported → {outputPath}");
            if (await ConfirmDialog.Show(this, "Export Complete",
                    $"Exported to:\n{outputPath}", "Open folder", "Close"))
                RevealInFolder(outputPath);
        }
        else if (cancelled)
        {
            SetStatus("Export cancelled");
        }
        else
        {
            SetStatus($"Export failed: {error}");
            await MessageDialog.Show(this, "Export Failed", $"The export could not be completed:\n{error}");
        }
    }

    // ── Export queue (PLAN.md step 29) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// File ▸ Export Queue… (Ctrl+Shift+E): opens the batch-export window (PLAN.md step 29). The queue runs jobs
    /// sequentially on the same background export path the single Export command uses; jobs can differ in output
    /// path, delivery format/quality, and target sequence. One reusable window + queue is kept per session.
    /// </summary>
    private void OpenExportQueue()
    {
        if (_project is null)
            return;

        EnsureExportQueue();
        if (_exportQueueWindow is null)
        {
            _exportQueueWindow = new ExportQueueWindow(_exportQueue!, AddToQueueAsync, RunExportQueueAsync);
            _exportQueueWindow.Closed += (_, _) => _exportQueueWindow = null;
            _exportQueueWindow.Show(this);
        }
        else
        {
            _exportQueueWindow.Activate();
        }
    }

    /// <summary>Builds the session's export queue on first use. The runner renders each job through
    /// <see cref="VideoExporter"/> over the current project — the same deterministic path the single Export uses,
    /// honouring the job's target sequence and in-out range.</summary>
    private void EnsureExportQueue()
    {
        _exportQueue ??= new Export.ExportQueue((job, progress, ct) =>
            VideoExporter.Export(_project!, job.OutputPath, job.Options, job.SequenceId, job.Range, progress, ct));
    }

    /// <summary>The queue window's "Add…" action: pick a delivery format then an output file, and enqueue a job for
    /// the active sequence. Repeating after switching the active sequence queues a different sequence — the job
    /// captures the sequence id, so each runs against its own sequence.</summary>
    private async Task AddToQueueAsync()
    {
        if (_project is null)
            return;

        Window owner = (Window?)_exportQueueWindow ?? this;
        Sequence sequence = _project.ActiveSequence;
        if (sequence.Timeline.Duration <= Timecode.Zero)
        {
            await MessageDialog.Show(owner, "Nothing to Queue",
                "The active sequence is empty — add a clip before queuing an export.");
            return;
        }

        Resolution res = sequence.Timeline.Resolution;
        if (await ExportSettingsDialog.Show(owner, res.Width, res.Height) is not { } options)
            return;

        ExportFormat format = options.Format;
        string extension = format.FileExtension;
        var fileType = new FilePickerFileType($"{ExportCodecs.Container(format.Container).DisplayName} video")
        {
            Patterns = ["*" + extension],
            MimeTypes = [format.MimeType],
        };
        string baseName = _currentProjectPath is null ? _projectName : ProjectDisplayName(_currentProjectPath);
        IStorageFile? target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Queue Export",
            SuggestedFileName = $"{baseName} - {sequence.Name}{extension}",
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [fileType],
        });
        if (target?.TryGetLocalPath() is not { } outputPath)
            return;

        EnsureExportQueue();
        string name = $"{sequence.Name} · {ExportCodecs.Container(format.Container).DisplayName}";
        _exportQueue!.Enqueue(outputPath, options, sequenceId: sequence.Id, range: null, name: name);
        SetStatus($"Queued export → {Path.GetFileName(outputPath)}");
    }

    /// <summary>The queue window's "Start" action: quiesce every in-process decode pipeline (as the single export
    /// does — a second concurrent libav* pipeline crashes the muxer), run the queue to completion on the background
    /// export path, then resume. Guarded so a run never overlaps another export.</summary>
    private async Task RunExportQueueAsync()
    {
        if (_project is null || _exportQueue is null || _exporting || !_exportQueue.HasPending)
            return;

        _exporting = true;
        SetEnabled(false);

        bool sourceWasActive = ReferenceEquals(_active, _source);
        _source?.Deactivate();
        if (_engine is not null)
            await _engine.SuspendAsync();

        try
        {
            await _exportQueue.RunAsync();
        }
        finally
        {
            _engine?.Resume();
            if (sourceWasActive)
                _source?.Activate();
            _exporting = false;
            SetEnabled(true);
        }

        IReadOnlyList<ExportJob> jobs = _exportQueue.Jobs;
        int done = jobs.Count(j => j.Status == ExportJobStatus.Succeeded);
        int failed = jobs.Count(j => j.Status == ExportJobStatus.Failed);
        int cancelled = jobs.Count(j => j.Status == ExportJobStatus.Cancelled);
        string summary = $"Export queue finished — {done} exported";
        if (failed > 0) summary += $", {failed} failed";
        if (cancelled > 0) summary += $", {cancelled} cancelled";
        SetStatus(summary);
    }

    private enum InterchangeKind { Edl, FinalCutXml }

    /// <summary>
    /// File ▸ Export Interchange ▸ EDL / Final Cut XML (PLAN.md step 28): writes the active sequence to an
    /// interchange format for round-tripping cuts with other NLEs. Interchange is a pure model→format mapping (no
    /// FFmpeg / in-process muxer), so — unlike the video export — it needn't quiesce the playback pipeline. Anything
    /// the format can't carry is reported back to the user rather than silently dropped.
    /// </summary>
    private async Task ExportInterchangeAsync(InterchangeKind kind)
    {
        if (_project is null)
            return;
        if (_project.Timeline.Duration.Ticks <= 0)
        {
            await MessageDialog.Show(this, "Nothing to Export",
                "The timeline is empty — add a clip before exporting an interchange file.");
            return;
        }

        (string label, string extension, FilePickerFileType fileType) = kind switch
        {
            InterchangeKind.Edl => ("EDL", ".edl",
                new FilePickerFileType("CMX3600 EDL") { Patterns = ["*.edl"] }),
            _ => ("Final Cut XML", ".xml",
                new FilePickerFileType("Final Cut XML") { Patterns = ["*.xml", "*.fcpxml"] }),
        };

        string baseName = _currentProjectPath is null ? _projectName : ProjectDisplayName(_currentProjectPath);
        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {label}",
            SuggestedFileName = baseName + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [fileType],
        });
        if (target?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            InterchangeReport report = kind == InterchangeKind.Edl
                ? EdlExporter.Save(_project, path)
                : FinalCutXmlInterchange.Save(_project, path);

            SetStatus($"Exported {label} → {path}");
            if (report.HasWarnings)
                await MessageDialog.Show(this, $"{label} Exported — some details were not included",
                    $"Exported to:\n{path}\n\n{label} cannot represent everything in this sequence:\n\n"
                    + string.Join("\n", report.Warnings.Select(w => "• " + w)));
        }
        catch (Exception ex)
        {
            SetStatus($"{label} export failed: {ex.Message}");
            await MessageDialog.Show(this, "Export Failed", $"The {label} export could not be completed:\n{ex.Message}");
        }
    }

    /// <summary>
    /// File ▸ Relink Media (PLAN.md step 28): re-points the project's offline sources at files under a folder the
    /// user picks, matching by file name (disambiguated by path tail). Offline sources are those with no local path
    /// or a path that no longer resolves — they render as black/silence until relinked (§15). The matches are
    /// previewed for confirmation before anything is applied, and the relinked paths (a per-user concern) are
    /// written straight to the media-link sidecar, not the shared project file.
    /// </summary>
    private async Task RelinkMediaAsync()
    {
        if (_project is null)
            return;

        IReadOnlyList<OfflineMedia> offline = MediaRelink.FindOffline(_project);
        if (offline.Count == 0)
        {
            await MessageDialog.Show(this, "No Offline Media",
                "Every source in this project resolves to a file on disk — there is nothing to relink.");
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Relink {offline.Count} offline media file{(offline.Count == 1 ? "" : "s")} — choose a folder to search",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } root)
            return;

        RelinkPlan plan = await Task.Run(() => MediaRelink.Plan(_project, root));
        if (plan.Matches.Count == 0)
        {
            await MessageDialog.Show(this, "No Matches Found",
                $"No files under the chosen folder matched the {offline.Count} offline source"
                + $"{(offline.Count == 1 ? "" : "s")} by name.");
            return;
        }

        string preview = "Found matches for " + plan.Matches.Count + " of " + offline.Count + " offline source(s):\n\n"
            + string.Join("\n", plan.Matches.Take(12).Select(m => $"• {Path.GetFileName(m.NewPath)}"))
            + (plan.Matches.Count > 12 ? $"\n… (+{plan.Matches.Count - 12} more)" : "")
            + (plan.Ambiguous.Count > 0 ? $"\n\n{plan.Ambiguous.Count} ambiguous (several candidates) — left offline." : "")
            + (plan.Unmatched.Count > 0 ? $"\n{plan.Unmatched.Count} not found — left offline." : "");
        if (!await ConfirmDialog.Show(this, "Relink Media", preview, "Relink", "Cancel"))
            return;

        int relinked = MediaRelink.Apply(_project, plan);

        // Relinked paths are per-user state; persist them to the sidecar (not the shared, diffable project file).
        if (_currentProjectPath is not null)
        {
            try { MediaLinks.Write(_project, _currentProjectPath); }
            catch (Exception ex) { SetStatus($"Relinked {relinked}, but the media-link sidecar could not be saved: {ex.Message}"); }
        }

        _mediaBrowser?.Refresh();
        SetStatus($"Relinked {relinked} media file{(relinked == 1 ? "" : "s")}"
            + (_currentProjectPath is null ? " (save the project to keep the links)." : "."));
    }

    /// <summary>Opens the OS file manager with <paramref name="path"/> selected (Explorer/Finder), or its folder
    /// on Linux. Best-effort: revealing the output is a convenience and must never throw into the export flow.</summary>
    private static void RevealInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false });
            else if (Path.GetDirectoryName(path) is { } dir)
                Process.Start(new ProcessStartInfo("xdg-open", [dir]) { UseShellExecute = false });
        }
        catch { /* the file manager may be missing/locked down — surfacing the file is non-essential */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private void SetEnabled(bool enabled)
    {
        // ProgramTab/SourceTab are included so a tab switch can't spin up the Source monitor's decoder while an
        // export's in-process muxer is running (a second concurrent libav* pipeline crashes the muxer).
        foreach (string name in new[] { "PlayPauseButton", "JumpStartButton", "JumpEndButton", "StepBackButton", "StepForwardButton", "Scrubber", "AddTrackButton", "ProgramTab", "SourceTab" })
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

    /// <summary>Whether an export (single or a queue run) is in flight — swapping the session (New / Open) while a
    /// background export reads the current project would tear its engine/media out from under it, so those actions
    /// no-op with a hint until the export finishes or is cancelled.</summary>
    private bool BlockedByExport()
    {
        if (!_exporting)
            return false;
        SetStatus("An export is in progress — cancel or wait for it to finish first.");
        return true;
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;

    private static string FormatTime(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
