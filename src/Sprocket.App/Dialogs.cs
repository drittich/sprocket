using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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

/// <summary>The "About Sprocket" box. Deliberately carries no framework / runtime text (UI.md §3.7) — just the
/// product name, the app's own version, and a one-line description.</summary>
internal static class AboutDialog
{
    public static Task Show(Window owner)
    {
        var logo = new Border
        {
            Width = 40,
            Height = 40,
            Background = Palette.Accent,
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "S",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
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
            Width = 380,
            Height = 240,
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
                    Centered("A non-destructive, cross-platform video editor.", 12, FontWeight.Normal, Palette.MutedText),
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
