using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Sprocket.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MediaBootstrap.Result result = MediaBootstrap.Create(desktop.Args ?? []);
            var window = new MainWindow(result.Engine, result.Project, result.Status);
            desktop.MainWindow = window;

            // Tear the engine (and its decode worker) down cleanly when the window closes.
            if (result.Engine is { } engine)
                desktop.ShutdownRequested += async (_, _) => await engine.DisposeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
