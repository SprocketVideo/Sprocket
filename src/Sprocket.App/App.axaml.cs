using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

            // Building the launch session can be slow on first run — the sample clip is generated with the
            // ffmpeg CLI (a few seconds). Show a splash immediately and build the session off the UI thread so
            // the splash renders/animates rather than the app appearing not to start. We do NOT set
            // desktop.MainWindow yet, so the lifetime's auto-show is a no-op; the splash is shown manually and
            // the editor shell is shown (and the splash dismissed) once the session is ready.
            var splash = new InitializingWindow();
            splash.Show();
            _ = InitializeFirstSessionAsync(splash, desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the launch session (opening the CLI media arg, or generating + opening a sample clip) on a
    /// background thread so the <see cref="InitializingWindow"/> splash stays responsive, then swaps in the
    /// editor shell on the UI thread and dismisses the splash. <see cref="MediaBootstrap.Create"/> already
    /// degrades to an empty project rather than throwing, but we still guard so a failure can't strand the user
    /// on the splash. The shell is shown before the splash closes so the window count never hits zero (which
    /// would trip the last-window-closes shutdown).
    /// </summary>
    private async Task InitializeFirstSessionAsync(Window splash, string[] args)
    {
        MediaBootstrap.Result result = await Task.Run(() =>
        {
            try { return MediaBootstrap.Create(args); }
            catch (Exception ex) { return new MediaBootstrap.Result(null, null, $"Failed to start a session: {ex.Message}"); }
        });

        void Finish()
        {
            _proxy = result.Proxy;
            MainWindow window = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null, result.Proxy);
            if (_desktop is not null)
                _desktop.MainWindow = window;
            window.Show();
            splash.Close();
        }

        // Task.Run resumes on the captured UI context, but marshal explicitly so window construction is
        // unambiguously on the UI thread regardless of how the continuation is scheduled.
        if (Dispatcher.UIThread.CheckAccess())
            Finish();
        else
            await Dispatcher.UIThread.InvokeAsync(Finish);
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
