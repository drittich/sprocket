using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Sprocket.Spike;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var preview = this.FindControl<BrightnessPreviewControl>("Preview")!;
        var stats = this.FindControl<TextBlock>("Stats")!;
        preview.StatsUpdated += text => stats.Text = text;

        // Optional timed auto-exit for unattended profiling runs: SPROCKET_SPIKE_SECONDS=12
        if (int.TryParse(Environment.GetEnvironmentVariable("SPROCKET_SPIKE_SECONDS"), out int seconds) && seconds > 0)
        {
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
        }
    }
}
