using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sprocket.App.Proxy;
using Sprocket.Core.Model;
using Sprocket.Playback;

namespace Sprocket.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private PlaybackEngine? _engine; // the live session's engine; swapped on File ▸ New / Open (PLAN.md step 16c)
    private ProxyService? _proxy;    // the live session's proxy service (PLAN.md step 18); swapped alongside the engine

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownRequested += OnShutdownRequested;

            // Launch is fast now: an empty, importable project (or a file passed on the command line) — no sample
            // clip is generated, so there is nothing slow to cover and no splash. Build the session synchronously
            // and hand the shell to the lifetime, which shows it. MediaBootstrap.Create degrades to an empty
            // project rather than throwing, so this can't strand the user.
            MediaBootstrap.Result result = MediaBootstrap.Create(desktop.Args ?? []);
            desktop.MainWindow = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null, result.Proxy);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds a shell window over a session and tracks the session engine + proxy service for teardown / reload.</summary>
    private MainWindow BuildWindow(PlaybackEngine? engine, Project? project, string status, string? projectPath, ProxyService? proxy)
    {
        _engine = engine;
        _proxy = proxy;
        var window = new MainWindow(engine, project, status, projectPath, proxy);
        window.SessionRequested += OnSessionRequested;
        return window;
    }

    /// <summary>
    /// File ▸ New / Open hands us a fully-built project; we build a fresh engine over it (PLAN.md step 16c) and
    /// swap the shell window. The new window is shown before the old one closes (so the last-window-closes
    /// shutdown never trips), then the previous engine + its decode/audio workers are disposed.
    /// </summary>
    private async void OnSessionRequested(MainWindow.SessionRequest request)
    {
        if (_desktop is null)
            return;

        Window? oldWindow = _desktop.MainWindow;
        PlaybackEngine? oldEngine = _engine;
        ProxyService? oldProxy = _proxy;

        MediaBootstrap.Result result = MediaBootstrap.CreateForProject(request.Project, request.Status);
        MainWindow window = BuildWindow(result.Engine, result.Project, request.Status, request.ProjectPath, result.Proxy);
        _desktop.MainWindow = window;
        window.Show();

        oldWindow?.Close();
        oldProxy?.Dispose(); // stop the previous session's proxy worker before its engine tears down
        if (oldEngine is not null)
            await oldEngine.DisposeAsync();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _proxy?.Dispose();
        if (_engine is { } engine)
            await engine.DisposeAsync();
    }
}
