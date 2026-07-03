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
    private UpdateCheckService? _updates; // app-scoped update checker (PLAN.md step 45); survives session swaps

    /// <summary>The MCP server controller, for the shell's status-bar indicator.</summary>
    internal McpServerService? McpService => _mcp;

    /// <summary>The update checker, for the shell's status-bar badge and Help ▸ Check for Updates.</summary>
    internal UpdateCheckService? UpdateService => _updates;

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
            _updates = new UpdateCheckService(); // before BuildWindow, so the first window can wire its badge
            CliOptions cli = CliOptions.Parse(desktop.Args ?? []);
            if (cli.Error is not null)
                Console.Error.WriteLine($"mcp: {cli.Error}");
            MediaBootstrap.Result result = MediaBootstrap.Create(cli.MediaPath);
            desktop.MainWindow = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null, result.Proxy, result.AudioClock);

            // Start the MCP server only on an explicit user switch (PLAN.md step 38): the persisted Preferences
            // toggle, or the --mcp / --mcp-port scripting flags. The CLI override is session-only — it is never
            // written back to the settings file, and a later Preferences apply supersedes it.
            UserSettings startupSettings = cli.ApplyTo(
                UserSettingsFile.Load(), Environment.GetEnvironmentVariable(CliOptions.McpTokenEnvVar));
            _ = StartMcpAsync(_mcp, startupSettings, announce: cli.McpRequested);

            // Update check (PLAN.md step 45): fire-and-forget after the shell is handed to the lifetime —
            // never blocks startup; throttled across launches; the window's badge mirrors the result.
            _ = _updates.CheckAsync(startupSettings, force: false);
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

    /// <summary>
    /// Applies the startup MCP settings and — when the server was requested from the command line — prints a
    /// single readiness line to stdout, so a launch-and-connect script can wait for the port deterministically
    /// instead of sleeping and retrying (failures go to stderr with a non-ambiguous prefix).
    /// </summary>
    private static async Task StartMcpAsync(McpServerService mcp, UserSettings settings, bool announce)
    {
        await mcp.ApplyAsync(settings);
        if (!announce)
            return;
        if (mcp.State == McpServerService.McpState.Listening)
            Console.WriteLine($"mcp: listening on http://127.0.0.1:{mcp.Port}/mcp");
        else if (mcp.State == McpServerService.McpState.Error)
            Console.Error.WriteLine($"mcp: failed to start on port {mcp.Port}: {mcp.LastError}");
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _updates?.Dispose(); // cancel any in-flight update check
        if (_mcp is { } mcp)
            await mcp.DisposeAsync(); // stop accepting AI edits before the session tears down
        _proxy?.Dispose();
        if (_engine is { } engine)
            await engine.DisposeAsync();
    }
}
