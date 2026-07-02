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
    private McpServerService? _mcp;  // app-scoped MCP server controller (PLAN.md step 38); survives session swaps

    /// <summary>The MCP server controller, for the shell's status-bar indicator.</summary>
    internal McpServerService? McpService => _mcp;

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
            _mcp = new McpServerService();
            MediaBootstrap.Result result = MediaBootstrap.Create(desktop.Args ?? []);
            desktop.MainWindow = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null, result.Proxy, result.AudioClock);

            // Honour the persisted MCP toggle (PLAN.md step 38): applying the stored settings IS the user's
            // switch — the server is never started unless that toggle was left on.
            _ = _mcp.ApplyAsync(UserSettingsFile.Load());
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds a shell window over a session and tracks the session engine + proxy service for teardown / reload.</summary>
    private MainWindow BuildWindow(
        PlaybackEngine? engine, Project? project, string status, string? projectPath, ProxyService? proxy,
        Sprocket.Audio.AudioEngine? audioClock)
    {
        _engine = engine;
        _proxy = proxy;
        var window = new MainWindow(engine, project, status, projectPath, proxy, audioClock);
        window.SessionRequested += OnSessionRequested;
        _mcp?.AttachSession(window.CreateMcpSession()); // re-point the MCP server at the new session
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

        try
        {
            MediaBootstrap.Result result = MediaBootstrap.CreateForProject(request.Project, request.Status);
            MainWindow window = BuildWindow(result.Engine, result.Project, request.Status, request.ProjectPath, result.Proxy, result.AudioClock);
            _desktop.MainWindow = window;
            window.Show();

            oldWindow?.Close();
            oldProxy?.Dispose(); // stop the previous session's proxy worker before its engine tears down
            if (oldEngine is not null)
                await oldEngine.DisposeAsync();
        }
        catch (Exception ex)
        {
            // This is an async void handler: an escaped exception would terminate the whole editor. Building a
            // session opens decoders / an audio device — a failure there (a bad file, an unstable device) must
            // not take the app down. Log it and leave the current session in place rather than crashing mid-swap.
            CrashLog.Write("Failed to open session", ex);
        }
    }

    /// <summary>Applies the MCP fields of the user settings — starts, stops, or restarts the loopback MCP
    /// server (PLAN.md step 38). Called by the Preferences dialog and once at startup.</summary>
    internal void ApplyMcpSettings(UserSettings settings) => _ = _mcp?.ApplyAsync(settings);

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_mcp is { } mcp)
            await mcp.DisposeAsync(); // stop accepting AI edits before the session tears down
        _proxy?.Dispose();
        if (_engine is { } engine)
            await engine.DisposeAsync();
    }
}
