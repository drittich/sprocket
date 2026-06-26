using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Persistence;
using Sprocket.Playback;

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

    // ── Menu + keyboard ────────────────────────────────────────────────────────────────────────────

    private void WireMenu()
    {
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

        var media = _project.MediaPool.Items.ToList();
        this.FindControl<ListBox>("MediaList")!.ItemsSource =
            media.Select(m => Path.GetFileName(m.AbsolutePath)).ToList();
        this.FindControl<TextBlock>("ProjectItemsText")!.Text =
            media.Count == 1 ? "1 item" : $"{media.Count} items";

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

    // ── Transport ───────────────────────────────────────────────────────────────────────────────────

    private void WireTransport()
    {
        var playPause = this.FindControl<Button>("PlayPauseButton")!;
        var scrubber = this.FindControl<Slider>("Scrubber")!;
        var positionText = this.FindControl<TextBlock>("PositionText")!;
        var durationText = this.FindControl<TextBlock>("DurationText")!;
        var preview = this.FindControl<PreviewSurface>("Preview")!;

        preview.Attach(_engine!);
        WireTimeline();

        _exportButton!.Click += (_, _) => _ = ExportAsync();
        WireAddTrackButton();

        Timecode duration = _engine!.Duration;
        scrubber.Maximum = Math.Max(1, duration.Ticks);
        durationText.Text = FormatTime(duration);
        positionText.Text = FormatTime(Timecode.Zero);

        playPause.Click += (_, _) => _engine.TogglePlayPause();
        this.FindControl<Button>("JumpStartButton")!.Click += (_, _) => _engine.SeekTo(Timecode.Zero);
        this.FindControl<Button>("JumpEndButton")!.Click += (_, _) => _engine.SeekTo(_engine.Duration);

        scrubber.ValueChanged += (_, e) =>
        {
            if (_suppressSeek)
                return;
            _engine.SeekTo(new Timecode((long)e.NewValue));
        };

        _engine.PositionChanged += pos => Dispatcher.UIThread.Post(() =>
        {
            _suppressSeek = true;
            scrubber.Value = Math.Clamp(pos.Ticks, 0, scrubber.Maximum);
            _suppressSeek = false;
            positionText.Text = FormatTime(pos);
        });

        _engine.StateChanged += state => Dispatcher.UIThread.Post(() =>
        {
            playPause.Content = state == PlaybackState.Playing ? "❚❚" : "▶";
            _engineStateText!.Text = state == PlaybackState.Playing ? "Playing" : "Paused";
        });

        // Optional timed auto-exit for unattended profiling runs: SPROCKET_APP_SECONDS=12
        if (int.TryParse(Environment.GetEnvironmentVariable("SPROCKET_APP_SECONDS"), out int seconds) && seconds > 0)
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Binds the custom timeline control to the project / edit history / engine and connects the timeline's
    /// chrome (zoom buttons, the Snapping toggle) and its selection back to the shell.
    /// </summary>
    private void WireTimeline()
    {
        var timeline = this.FindControl<TimelineControl>("Timeline")!;
        timeline.Attach(_project!, _history, _engine);

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
            string? name = clip is null ? null : Path.GetFileName(_project!.MediaPool.Get(clip.MediaRefId)?.AbsolutePath ?? "clip");
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
        foreach (string name in new[] { "PlayPauseButton", "JumpStartButton", "JumpEndButton", "Scrubber", "AddTrackButton" })
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
