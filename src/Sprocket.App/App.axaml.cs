using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sprocket.Core.Model;
using Sprocket.Playback;

namespace Sprocket.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private PlaybackEngine? _engine; // the live session's engine; swapped on File ▸ New / Open (PLAN.md step 16c)

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            MediaBootstrap.Result result = MediaBootstrap.Create(desktop.Args ?? []);
            // The classic-desktop lifetime shows desktop.MainWindow itself, so don't call Show() for the first one.
            desktop.MainWindow = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null);
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds a shell window over a session and tracks the session engine for teardown / reload.</summary>
    private MainWindow BuildWindow(PlaybackEngine? engine, Project? project, string status, string? projectPath)
    {
        _engine = engine;
        var window = new MainWindow(engine, project, status, projectPath);
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

        MediaBootstrap.Result result = MediaBootstrap.CreateForProject(request.Project, request.Status);
        MainWindow window = BuildWindow(result.Engine, result.Project, request.Status, request.ProjectPath);
        _desktop.MainWindow = window;
        window.Show();

        oldWindow?.Close();
        if (oldEngine is not null)
            await oldEngine.DisposeAsync();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_engine is { } engine)
            await engine.DisposeAsync();
    }
}
