using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sprocket.App;

/// <summary>
/// Small modal dialogs for the menu surface (PLAN.md step 16c): an About box and a discard-unsaved-changes
/// confirmation. Built in code (like <see cref="Timeline.TimelineControl"/> / the panels) against the shell's
/// dark palette, so there is no extra XAML. Dialog look/behaviour rests on manual verification (the App is a
/// UI-bound WinExe); the logic that decides *whether* to show them lives in testable helpers.
/// </summary>
internal static class Palette
{
    public static readonly IBrush WindowBg = new SolidColorBrush(Color.Parse("#0E0E12"));
    public static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#16161C"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D5DBE6"));
    public static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#9AA4B2"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4227a3"));
}

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
            Background = Palette.Accent,
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
            Background = Palette.WindowBg,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    logo,
                    Centered("Sprocket", 20, FontWeight.SemiBold, Palette.Text),
                    Centered($"Version {Program.AppVersion}", 12, FontWeight.Normal, Palette.MutedText),
                    Centered($"Media engine: {ffmpeg}", 12, FontWeight.Normal, Palette.MutedText),
                    Centered("A cross-platform, non-destructive video editor — free and open source.", 12, FontWeight.Normal, Palette.MutedText),
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
            Background = Palette.Accent,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = cancelText,
            Padding = new Thickness(16, 5),
            Foreground = Palette.Text,
            Background = Palette.PanelBg,
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
            Background = Palette.WindowBg,
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
                        Foreground = Palette.Text,
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
