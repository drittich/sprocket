using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Playback;

namespace Sprocket.App;

public partial class MainWindow : Window
{
    private readonly PlaybackEngine? _engine;
    private readonly Project? _project;
    private bool _suppressSeek;   // guards programmatic scrubber updates from re-triggering a seek
    private bool _exporting;

    // Parameterless ctor for the XAML designer / tooling.
    public MainWindow() : this(null, null, string.Empty) { }

    public MainWindow(PlaybackEngine? engine, Project? project, string status)
    {
        AvaloniaXamlLoader.Load(this);
        _engine = engine;
        _project = project;

        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var playPause = this.FindControl<Button>("PlayPauseButton")!;
        var scrubber = this.FindControl<Slider>("Scrubber")!;
        var positionText = this.FindControl<TextBlock>("PositionText")!;
        var durationText = this.FindControl<TextBlock>("DurationText")!;
        var exportButton = this.FindControl<Button>("ExportButton")!;
        var preview = this.FindControl<PreviewSurface>("Preview")!;

        statusText.Text = status;

        if (_engine is null)
        {
            playPause.IsEnabled = false;
            scrubber.IsEnabled = false;
            exportButton.IsEnabled = false;
            return;
        }

        exportButton.IsEnabled = _project is not null;
        exportButton.Click += (_, _) => _ = ExportAsync(exportButton, statusText);

        preview.Attach(_engine);

        Timecode duration = _engine.Duration;
        scrubber.Maximum = Math.Max(1, duration.Ticks);
        durationText.Text = FormatTime(duration);
        positionText.Text = FormatTime(Timecode.Zero);

        playPause.Click += (_, _) => _engine.TogglePlayPause();

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
            playPause.Content = state == PlaybackState.Playing ? "❚❚" : "▶");

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                _engine.TogglePlayPause();
                e.Handled = true;
            }
        };

        // Optional timed auto-exit for unattended profiling runs: SPROCKET_APP_SECONDS=12
        if (int.TryParse(Environment.GetEnvironmentVariable("SPROCKET_APP_SECONDS"), out int seconds) && seconds > 0)
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Exports the loaded project to an <c>.mp4</c> next to the app output on a background thread (export is
    /// CPU-bound and must not block the UI), pausing playback first and streaming progress to the status strip.
    /// The actual pipeline lives in <see cref="VideoExporter"/>; this is just the composition-root trigger.
    /// </summary>
    private async Task ExportAsync(Button exportButton, TextBlock statusText)
    {
        if (_exporting || _project is null)
            return;

        _exporting = true;
        exportButton.IsEnabled = false;
        _engine?.Pause();

        string outputPath = Path.Combine(AppContext.BaseDirectory, "export.mp4");
        var progress = new Progress<double>(p => statusText.Text = $"Exporting… {p * 100:0}%");

        try
        {
            await Task.Run(() => VideoExporter.Export(_project, outputPath, progress: progress));
            statusText.Text = $"Exported → {outputPath}";
        }
        catch (Exception ex)
        {
            statusText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            _exporting = false;
            exportButton.IsEnabled = true;
        }
    }

    private static string FormatTime(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
