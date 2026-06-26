using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
/// live telemetry. The Program monitor + transport, Export, Save, and Edit ▸ Undo/Redo are fully wired; the
/// pane *contents* (media bin, timeline control, inspector) arrive in steps 12–16. Every model mutation runs
/// through <see cref="EditHistory"/>, so the dirty indicator and undo/redo are correct by construction.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlaybackEngine? _engine;
    private readonly Project? _project;
    private readonly EditHistory _history = new();

    private ThumbnailService? _thumbnails;
    private MediaBrowserPanel? _mediaBrowser;
    private InspectorPanel? _inspector;

    // Dual monitors (PLAN.md step 17): the Program monitor wraps the main engine; the Source monitor previews
    // the selected clip's source. The transport bar drives whichever is active.
    private ProgramMonitor? _program;
    private SourceMonitor? _source;
    private IMonitor? _active;
    private PreviewSurface? _preview;
    private Button? _playPause;
    private Slider? _scrubber;
    private TextBlock? _positionText, _durationText;

    private bool _suppressSeek;        // guards programmatic scrubber updates from re-triggering a seek
    private bool _exporting;
    private int _savedUndoCount;       // history depth at the last save; document is clean while it matches
    private string _projectName = "Untitled";

    // Controls captured for later updates.
    private TextBlock? _statusText, _telemetryText, _engineStateText, _saveStateText, _timelineHeader;
    private MenuItem? _undoMenuItem, _redoMenuItem;
    private Button? _exportButton, _maxButton;
    private Control? _root;

    // Parameterless ctor for the XAML designer / tooling.
    public MainWindow() : this(null, null, string.Empty) { }

    public MainWindow(PlaybackEngine? engine, Project? project, string status)
    {
        AvaloniaXamlLoader.Load(this);
        _engine = engine;
        _project = project;

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

        WireWindowChrome();
        WireMenu();
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ForceFirstFrameComposite();
    }

    /// <summary>
    /// Works around an Avalonia 12 compositor regression (AvaloniaUI/Avalonia#20726, #8123) where controls can
    /// stay unpainted on the first composited frame until a layout/composition pass is forced — the same bug
    /// users "fix" by hovering or resizing the window. The GPU custom-draw <see cref="PreviewSurface"/> makes it
    /// deterministic for its DockPanel sibling, the transport bar. A render-only <c>InvalidateVisual</c> is not
    /// enough (the scene node is never committed), so a few frames after the window is shown we force a full
    /// layout pass plus a 1px size nudge (the reliable recovery), then stop.
    /// </summary>
    private void ForceFirstFrameComposite()
    {
        int tick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            switch (++tick)
            {
                case 1 when !double.IsNaN(Width): Width += 1; break; // a real size change rebuilds the scene
                case 2 when !double.IsNaN(Width): Width -= 1; break; // restore
            }
            _root?.InvalidateMeasure();
            if (tick >= 3)
                timer.Stop();
        };
        timer.Start();
    }

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
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _thumbnails?.Dispose(); // releases the cached thumbnail bitmaps
        _ = _source?.DisposeAsync(); // tears down the Source monitor's decoder/engine if one is open
        base.OnClosed(e);
    }

    // ── Menu + keyboard ────────────────────────────────────────────────────────────────────────────

    private void WireMenu()
    {
        this.FindControl<MenuItem>("ImportMenuItem")!.Click += (_, _) => _ = ImportDialogAsync();
        this.FindControl<MenuItem>("SaveMenuItem")!.Click += (_, _) => Save();
        this.FindControl<MenuItem>("ExportMenuItem")!.Click += (_, _) => _ = ExportAsync();
        this.FindControl<MenuItem>("ExitMenuItem")!.Click += (_, _) => Close();
        this.FindControl<MenuItem>("AboutMenuItem")!.Click += (_, _) =>
            SetStatus("Sprocket — a non-destructive, cross-platform video editor.");
        _undoMenuItem!.Click += (_, _) => _history.Undo();
        _redoMenuItem!.Click += (_, _) => _history.Redo();

        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Space)
        {
            _engine?.TogglePlayPause();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            Save();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.E)
        {
            _ = ExportAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.I)
        {
            _ = ImportDialogAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && shift) // Ctrl+Shift+Z = redo
        {
            _history.Redo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z) // Ctrl+Z = undo
        {
            _history.Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y) // Ctrl+Y = redo
        {
            _history.Redo();
            e.Handled = true;
        }
    }

    // ── Project chrome: title, media bin, sequence badge, telemetry ─────────────────────────────────

    private void PopulateProjectChrome(string status)
    {
        SetStatus(status);

        if (_project is null)
            return;

        string? mediaPath = _project.MediaPool.Items.FirstOrDefault()?.AbsolutePath;
        _projectName = mediaPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(mediaPath);
        this.FindControl<TextBlock>("ProjectTitleText")!.Text = _projectName;

        Timeline timeline = _project.Timeline;
        double fps = Fps(timeline.FrameRate);
        (int w, int h) = (timeline.Resolution.Width, timeline.Resolution.Height);
        string resLabel = h switch { 2160 => "4K", 1080 => "1080p", 720 => "720p", _ => $"{w}×{h}" };
        this.FindControl<TextBlock>("SequenceBadge")!.Text = $"{resLabel} · {fps:0.##}";

        Timecode duration = _engine?.Duration ?? timeline.Duration;
        _telemetryText!.Text = $"{fps:0.##} fps · {w}×{h} · {FormatTime(duration)}";

        UpdateTimelineHeader();
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

        var guides = this.FindControl<ToggleButton>("GuidesToggle")!;
        guides.IsCheckedChanged += (_, _) => _preview!.ShowGuides = guides.IsChecked == true;
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
        timeline.Attach(_project!, _history, _engine);
        timeline.ClipPlaced += UpdateTimelineHeader; // a media-bin drop may extend the timeline

        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => timeline.ZoomIn();
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => timeline.ZoomOut();

        var snapping = this.FindControl<ToggleButton>("SnappingToggle")!;
        timeline.Snapping = snapping.IsChecked == true;
        snapping.IsCheckedChanged += (_, _) => timeline.Snapping = snapping.IsChecked == true;

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
            _mediaBrowser?.SetSelectedClip(clip); // the Effects browser applies to this clip
            _inspector?.SetSelectedClip(clip);    // the Inspector edits this clip's properties

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

    // ── File ops ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the project to <c>project.sprocket.json</c> next to the app output (PLAN.md step 9, slice DoD #8)
    /// and marks the document clean. A File-menu open/save-as dialog arrives with the full File menu surface.
    /// </summary>
    private void Save()
    {
        if (_project is null)
            return;

        string outputPath = Path.Combine(AppContext.BaseDirectory, "project.sprocket.json");
        try
        {
            ProjectSerializer.Save(_project, outputPath);
            _savedUndoCount = _history.UndoCount;
            OnHistoryChanged(); // refresh the dirty indicator
            SetStatus($"Saved → {outputPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
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
