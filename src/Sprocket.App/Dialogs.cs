using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Export;

namespace Sprocket.App;

// Small modal dialogs for the menu surface (PLAN.md step 16c): an About box and a discard-unsaved-changes
// confirmation. Built in code (like Timeline.TimelineControl / the panels) against the shell's dark palette —
// the shared Palette in Palette.cs, so there is no extra XAML and no per-dialog color copies. Dialog
// look/behaviour rests on manual verification (the App is a UI-bound WinExe); the logic that decides *whether*
// to show them lives in testable helpers.

/// <summary>The app icon, loaded once from the embedded avares resource and shared by the About box and any
/// code-built dialog windows. (MainWindow / its taskbar icon are wired directly in MainWindow.axaml.)</summary>
internal static class AppIcon
{
    public static readonly Bitmap Bitmap =
        new(AssetLoader.Open(new Uri("avares://Sprocket/Assets/sprocket.png")));

    public static WindowIcon Window => new(Bitmap);
}

/// <summary>The "About Sprocket" box. Deliberately carries no framework / runtime text (UI.md §3.7) — just the
/// product name, the app's own version, and a one-line description.</summary>
internal static class AboutDialog
{
    public static Task Show(Window owner)
    {
        // The bundled media engine version — a user-facing credit for the core dependency (not framework
        // chrome, UI.md §3.7). Degrades gracefully if FFmpeg can't be probed.
        string ffmpeg;
        try { ffmpeg = Sprocket.Media.FFmpegDiagnostics.DisplayVersion(); }
        catch { ffmpeg = "FFmpeg unavailable"; }

        var logo = new Image
        {
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = AppIcon.Bitmap,
        };

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(18, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = "About Sprocket",
            Icon = AppIcon.Window,
            Width = 380,
            Height = 270,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    logo,
                    Centered("Sprocket", 20, FontWeight.SemiBold, Palette.TextBrush),
                    Centered($"Version {Program.AppVersion}", 12, FontWeight.Normal, Palette.MutedTextBrush),
                    Centered($"Media engine: {ffmpeg}", 12, FontWeight.Normal, Palette.MutedTextBrush),
                    Centered("A cross-platform, non-destructive video editor. Free and open source.", 12, FontWeight.Normal, Palette.MutedTextBrush),
                    close,
                },
            },
        };

        close.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }

    private static TextBlock Centered(string text, double size, FontWeight weight, IBrush brush) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}

/// <summary>A two-button confirmation (e.g. discard unsaved changes before New/Open). Returns <c>true</c> when
/// the user accepts, <c>false</c> on cancel / close.</summary>
internal static class ConfirmDialog
{
    public static Task<bool> Show(Window owner, string title, string message, string confirmText, string cancelText)
    {
        var confirm = new Button
        {
            Content = confirmText,
            Padding = new Thickness(16, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = cancelText,
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = title,
            Icon = AppIcon.Window,
            Width = 400,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, confirm },
                    },
                    new TextBlock
                    {
                        Text = message,
                        Foreground = Palette.TextBrush,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(owner);
    }
}

/// <summary>A single-button information dialog (e.g. "export complete / failed"). Mirrors
/// <see cref="ConfirmDialog"/>'s look but has nothing to decide — it just acknowledges a message.</summary>
internal static class MessageDialog
{
    public static Task Show(Window owner, string title, string message, string buttonText = "OK")
    {
        var ok = new Button
        {
            Content = buttonText,
            Padding = new Thickness(18, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = title,
            Icon = AppIcon.Window,
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { ok },
                    },
                    new TextBlock
                    {
                        Text = message,
                        Foreground = Palette.TextBrush,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        ok.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }
}

/// <summary>
/// The Clip ▸ Speed / Duration dialog (PLAN.md step 21): edits a clip's playback speed as a percentage
/// (100% = normal), with quick presets. Returns the chosen speed as an exact <see cref="Rational"/>, or
/// <see langword="null"/> on cancel. Reverse and freeze (0%) are deferred, so the input is clamped to a
/// positive percentage.
/// </summary>
internal static class SpeedDialog
{
    public static Task<Rational?> Show(Window owner, Rational current)
    {
        var box = new TextBox
        {
            Text = SpeedFormat.ToPercentString(current),
            Width = 90,
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        foreach (int pct in new[] { 25, 50, 100, 200, 400 })
        {
            int p = pct;
            var b = new Button
            {
                Content = $"{p}%",
                Padding = new Thickness(10, 4),
                Foreground = Palette.TextBrush,
                Background = Palette.PanelBgBrush,
                CornerRadius = new CornerRadius(4),
            };
            b.Click += (_, _) => box.Text = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
            presets.Children.Add(b);
        }

        var ok = new Button
        {
            Content = "Apply",
            Padding = new Thickness(16, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = "Speed / Duration",
            Icon = AppIcon.Window,
            Width = 360,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, ok },
                    },
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Speed", Foreground = Palette.MutedTextBrush, FontSize = 12 },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                Children =
                                {
                                    box,
                                    new TextBlock { Text = "%", Foreground = Palette.TextBrush, VerticalAlignment = VerticalAlignment.Center },
                                },
                            },
                            presets,
                        },
                    },
                },
            },
        };

        void Accept()
        {
            if (SpeedFormat.TryParsePercent(box.Text, out Rational speed))
                dialog.Close(speed);
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close(null);
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Accept(); };

        return dialog.ShowDialog<Rational?>(owner);
    }
}

/// <summary>
/// The Sequence ▸ Settings dialog (PLAN.md step 23): shows the sequence's render format (read-only — a format
/// change would re-scale every clip, deferred) and lets the user rename it. Returns the trimmed new name on Apply,
/// or <see langword="null"/> on cancel / no change. The undoable rename itself is applied by the caller.
/// </summary>
internal static class SequenceSettingsDialog
{
    public static Task<string?> Show(Window owner, Sequence sequence)
    {
        var nameBox = new TextBox
        {
            Text = sequence.Name,
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Timeline t = sequence.Timeline;
        double fps = t.FrameRate.Den > 0 ? (double)t.FrameRate.Num / t.FrameRate.Den : 0;
        string format = $"{t.Resolution.Width}×{t.Resolution.Height}  ·  {fps:0.##} fps  ·  {t.SampleRate / 1000.0:0.#} kHz";

        var apply = new Button
        {
            Content = "Apply",
            Padding = new Thickness(16, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = "Sequence Settings",
            Icon = AppIcon.Window,
            Width = 380,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, apply },
                    },
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Name", Foreground = Palette.MutedTextBrush, FontSize = 12 },
                            nameBox,
                            new TextBlock { Text = "Format", Foreground = Palette.MutedTextBrush, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) },
                            new TextBlock { Text = format, Foreground = Palette.TextBrush, FontSize = 13 },
                        },
                    },
                },
            },
        };

        void Accept()
        {
            string trimmed = (nameBox.Text ?? string.Empty).Trim();
            dialog.Close(string.IsNullOrEmpty(trimmed) ? null : trimmed);
        }
        apply.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close(null);
        nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Accept(); };

        return dialog.ShowDialog<string?>(owner);
    }
}

/// <summary>
/// The Export settings dialog (PLAN.md step 27): a cascading container → video-codec → audio-codec picker plus a
/// quality tier, so the user can deliver into the whole format/codec matrix rather than a fixed MP4. The video /
/// audio dropdowns are repopulated with only the codecs valid in the chosen container, so every selection is a
/// valid combination. Returns the chosen <see cref="ExportOptions"/> on Export, or <see langword="null"/> on cancel.
/// </summary>
internal static class ExportSettingsDialog
{
    public static Task<ExportOptions?> Show(Window owner, int sequenceWidth, int sequenceHeight)
    {
        ExportContainer[] containers = Enum.GetValues<ExportContainer>();
        ComboBox containerBox = MakeCombo(containers.Select(c => ExportCodecs.Container(c).DisplayName));
        ComboBox videoBox = MakeCombo([]);
        ComboBox audioBox = MakeCombo([]);
        ComboBox qualityBox = MakeCombo(["High (larger file)", "Medium", "Low (smaller file)"]);
        qualityBox.SelectedIndex = 0;

        var resText = new TextBlock { Foreground = Palette.MutedTextBrush, FontSize = 12 };
        (int w, int h) = VideoExporter.ComputeExportResolution(sequenceWidth, sequenceHeight);
        resText.Text = (w == sequenceWidth && h == sequenceHeight)
            ? $"Output resolution: {w}×{h}"
            : $"Output resolution: {w}×{h}  (scaled from {sequenceWidth}×{sequenceHeight} to the 4K export cap)";

        // The codecs valid in the currently-selected container, mirrored so a selection index maps back to an enum.
        var videoCodecs = new List<ExportVideoCodec>();
        var audioCodecs = new List<ExportAudioCodec>();

        void RepopulateCodecs()
        {
            ExportContainer container = containers[Math.Max(0, containerBox.SelectedIndex)];
            videoCodecs = [.. ExportCodecs.VideoCodecsFor(container)];
            audioCodecs = [.. ExportCodecs.AudioCodecsFor(container)];
            videoBox.ItemsSource = videoCodecs.Select(c => ExportCodecs.Video(c).DisplayName).ToList();
            audioBox.ItemsSource = audioCodecs.Select(c => ExportCodecs.Audio(c).DisplayName).ToList();
            videoBox.SelectedIndex = videoCodecs.Count > 0 ? 0 : -1;
            audioBox.SelectedIndex = audioCodecs.Count > 0 ? 0 : -1;
        }

        containerBox.SelectionChanged += (_, _) => RepopulateCodecs();
        containerBox.SelectedIndex = 0;
        RepopulateCodecs(); // ensure populated even though setting index 0 (already 0) fires no change

        // Burn-ins & handles (PLAN.md step 29). Burn-ins are opt-in overlays baked onto the export (timecode /
        // clip name / watermark) with a nine-point position each; handles add extra frames around an in-out range
        // for review / conform outputs. Defaults keep the pre-step-29 behaviour (no burn-ins, no handles).
        BurnInPosition[] positions = Enum.GetValues<BurnInPosition>();
        var tcCheck = MakeCheck("Timecode");
        ComboBox tcPos = MakePositionCombo((int)BurnInPosition.BottomCenter);
        var nameCheck = MakeCheck("Clip name");
        ComboBox namePos = MakePositionCombo((int)BurnInPosition.TopLeft);
        var watermarkBox = new TextBox
        {
            PlaceholderText = "Watermark text…",
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ComboBox watermarkPos = MakePositionCombo((int)BurnInPosition.BottomRight);
        var handlesBox = new TextBox
        {
            Text = "0",
            Width = 70,
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
        };

        var export = new Button
        {
            Content = "Export…",
            Padding = new Thickness(16, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var settings = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("Format", containerBox),
                LabeledRow("Video codec", videoBox),
                LabeledRow("Audio codec", audioBox),
                LabeledRow("Quality", qualityBox),
                resText,
                new TextBlock { Text = "Burn-ins", Foreground = Palette.MutedTextBrush, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) },
                BurnInRow(tcCheck, tcPos),
                BurnInRow(nameCheck, namePos),
                BurnInRow(watermarkBox, watermarkPos),
                LabeledRow("Handles (frames before / after the range)", handlesBox),
            },
        };

        var dialog = new Window
        {
            Title = "Export Settings",
            Icon = AppIcon.Window,
            Width = 440,
            Height = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, export },
                    },
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = settings,
                    },
                },
            },
        };

        export.Click += (_, _) =>
        {
            if (containerBox.SelectedIndex < 0 || videoBox.SelectedIndex < 0 || audioBox.SelectedIndex < 0)
                return;
            var format = new ExportFormat(
                containers[containerBox.SelectedIndex],
                videoCodecs[videoBox.SelectedIndex],
                audioCodecs[audioBox.SelectedIndex]);
            var quality = (ExportQuality)Math.Max(0, qualityBox.SelectedIndex);

            var burnIns = new List<BurnIn>();
            if (tcCheck.IsChecked == true)
                burnIns.Add(new BurnIn(BurnInField.Timecode, positions[Math.Max(0, tcPos.SelectedIndex)]));
            if (nameCheck.IsChecked == true)
                burnIns.Add(new BurnIn(BurnInField.ClipName, positions[Math.Max(0, namePos.SelectedIndex)]));
            string watermark = (watermarkBox.Text ?? string.Empty).Trim();
            if (watermark.Length > 0)
                burnIns.Add(new BurnIn(BurnInField.Text, positions[Math.Max(0, watermarkPos.SelectedIndex)], watermark));

            int handles = 0;
            if (int.TryParse((handlesBox.Text ?? string.Empty).Trim(), out int parsed))
                handles = Math.Max(0, parsed);

            dialog.Close(new ExportOptions(
                Format: format,
                Quality: quality,
                HandleFrames: handles,
                BurnIns: burnIns.Count > 0 ? burnIns : null));
        };
        cancel.Click += (_, _) => dialog.Close((ExportOptions?)null);

        return dialog.ShowDialog<ExportOptions?>(owner);
    }

    private static ComboBox MakeCombo(IEnumerable<string> items) => new()
    {
        ItemsSource = items.ToList(),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Foreground = Palette.TextBrush,
        Background = Palette.PanelBgBrush,
    };

    /// <summary>A nine-point burn-in position picker, preselected to <paramref name="defaultIndex"/>.</summary>
    private static ComboBox MakePositionCombo(int defaultIndex)
    {
        ComboBox combo = MakeCombo(
        [
            "Top Left", "Top Center", "Top Right",
            "Middle Left", "Center", "Middle Right",
            "Bottom Left", "Bottom Center", "Bottom Right",
        ]);
        combo.Width = 130;
        combo.HorizontalAlignment = HorizontalAlignment.Right;
        combo.SelectedIndex = defaultIndex;
        return combo;
    }

    private static CheckBox MakeCheck(string label) => new()
    {
        Content = label,
        Foreground = Palette.TextBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A burn-in row: the enable control (checkbox or watermark textbox) on the left, its position picker
    /// pinned right.</summary>
    private static Grid BurnInRow(Control enable, Control position)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        enable.SetValue(Grid.ColumnProperty, 0);
        position.SetValue(Grid.ColumnProperty, 1);
        enable.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(enable);
        grid.Children.Add(position);
        return grid;
    }

    private static StackPanel LabeledRow(string label, Control control) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, FontSize = 12 },
            control,
        },
    };
}

/// <summary>
/// A modal progress dialog for an in-flight export: a determinate bar driven from the export's
/// <c>IProgress&lt;double&gt;</c> and a Cancel button that signals the supplied
/// <see cref="CancellationTokenSource"/>. While it is shown the shell is input-blocked, so no second
/// libav* pipeline can start. The dialog stays up (showing "Cancelling…") until the export actually
/// stops; only <see cref="CompleteAndClose"/> dismisses it — the window-chrome close button is treated
/// as a cancel so it can never orphan a running export.
/// </summary>
internal sealed class ExportProgressDialog : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _percent;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cts;
    private bool _allowClose;

    public ExportProgressDialog(string fileName, CancellationTokenSource cts)
    {
        _cts = cts;

        Title = "Exporting";
        Icon = AppIcon.Window;
        Width = 440;
        Height = 160;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.WindowBgBrush;

        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 16 };
        _percent = new TextBlock
        {
            Text = "0%",
            Foreground = Palette.MutedTextBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };
        _cancel.Click += (_, _) => RequestCancel();

        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _cancel.SetValue(DockPanel.DockProperty, Dock.Right);
        bottom.Children.Add(_cancel);
        bottom.Children.Add(_percent);

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"Exporting {fileName}…",
                    Foreground = Palette.TextBrush,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                _bar,
                bottom,
            },
        };
    }

    /// <summary>Updates the bar from an export progress fraction (0–1). Called on the UI thread by the
    /// caller's <c>Progress&lt;double&gt;</c>, which captured this thread's context at construction.</summary>
    public void SetProgress(double fraction)
    {
        int pct = (int)Math.Clamp(fraction * 100, 0, 100);
        _bar.Value = pct;
        if (!_cts.IsCancellationRequested)
            _percent.Text = $"{pct}%";
    }

    /// <summary>Dismisses the dialog once the export has finished — the only sanctioned way to close it.</summary>
    public void CompleteAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void RequestCancel()
    {
        _cancel.IsEnabled = false;
        _percent.Text = "Cancelling…";
        _cts.Cancel();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The title-bar close button must not orphan a running export: treat it as Cancel and keep the
        // dialog up until the export actually stops (CompleteAndClose then dismisses it).
        if (!_allowClose)
        {
            e.Cancel = true;
            if (!_cts.IsCancellationRequested)
                RequestCancel();
        }
        base.OnClosing(e);
    }
}
