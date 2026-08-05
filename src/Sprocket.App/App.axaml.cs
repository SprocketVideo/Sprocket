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
    private UpdateService? _updates;  // app-scoped Velopack updater (PLAN.md steps 36 + 45); survives session swaps
    private bool _tornDown;          // the session teardown has run; it must not run twice

    /// <summary>The MCP server controller, for the shell's status-bar indicator.</summary>
    internal McpServerService? McpService => _mcp;

    /// <summary>The updater, for the shell's status-bar badge and Help ▸ Check for Updates.</summary>
    internal UpdateService? UpdateService => _updates;

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
            _updates = new UpdateService(); // before BuildWindow, so the first window can wire its badge
            CliOptions cli = CliOptions.Parse(desktop.Args ?? []);
            if (cli.Error is not null)
                Console.Error.WriteLine($"mcp: {cli.Error}");
            UserSettings baseSettings = UserSettingsFile.Load();
            MediaBootstrap.Result result = MediaBootstrap.Create(cli.MediaPath, baseSettings.AudioOutputDevice);
            desktop.MainWindow = BuildWindow(result.Engine, result.Project, result.Status, projectPath: null, result.Proxy, result.AudioClock);

            // Start the MCP server only on an explicit user switch (PLAN.md step 38): the persisted Preferences
            // toggle, or the --mcp / --mcp-port scripting flags. The CLI override is session-only — it is never
            // written back to the settings file, and a later Preferences apply supersedes it.
            UserSettings startupSettings = cli.ApplyTo(
                baseSettings, Environment.GetEnvironmentVariable(CliOptions.McpTokenEnvVar));
            _ = StartMcpAsync(_mcp, startupSettings, announce: cli.McpRequested);

            // Update check (PLAN.md steps 36 + 45): fire-and-forget after the shell is handed to the
            // lifetime — never blocks startup; a no-op for portable/dev builds (not Velopack installs);
            // the window's badge mirrors the result.
            _ = _updates.CheckAsync(startupSettings, force: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds a shell window over a session and tracks the session engine + proxy service for teardown / reload.</summary>
    private MainWindow BuildWindow(
        PlaybackEngine? engine, Project? project, string status, string? projectPath, ProxyService? proxy,
        Sprocket.Audio.AudioEngine? audioClock, WindowPlacement? placement = null)
    {
        _engine = engine;
        _proxy = proxy;
        var window = new MainWindow(engine, project, status, projectPath, proxy, audioClock, placement);
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
            // Carry the outgoing window's live geometry, or the shell would jump to the persisted (last-close)
            // maximized-or-not at the XAML default size — a window maximized or resized since launch visibly
            // shrank on Open Sample Project. Opening a project must never move the window (WindowPlacement).
            WindowPlacement? placement = (oldWindow as MainWindow)?.CapturePlacement();

            // The new session opens on the current window's chosen output device (the persisted Preferences pick).
            string audioDevice = (oldWindow as MainWindow)?.AudioDeviceSetting ?? "";
            MediaBootstrap.Result result = MediaBootstrap.CreateForProject(request.Project, request.Status, audioDevice);
            MainWindow window = BuildWindow(result.Engine, result.Project, request.Status, request.ProjectPath, result.Proxy, result.AudioClock, placement);
            _desktop.MainWindow = window;
            window.Show();

            // The outgoing window still holds the document the user just chose to leave behind — they answered
            // the unsaved-changes prompt in File ▸ New / Open already, so its Closing gate must not re-ask.
            if (oldWindow is MainWindow replaced)
                replaced.ApproveClose();
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

    /// <summary>
    /// The application-level quit gate, and the session teardown that follows it.
    ///
    /// <para>Quit is guarded <i>here</i> rather than in the shell window's <c>Closing</c> handler because macOS
    /// does not route an app-level Quit (⌘Q, the Dock menu, log-out) through <c>windowShouldClose</c> —
    /// cancelling a window close there does not abort the quit (Avalonia #6149). <c>applicationShouldTerminate</c>,
    /// which Avalonia surfaces as this event, is the one hook all three platforms honour; on Windows / Linux it
    /// covers the quit that follows the last window closing, where <see cref="MainWindow.ApproveClose"/> has
    /// already recorded the user's answer.</para>
    /// </summary>
    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // No dialog can be awaited from here, so cancel this quit, ask, and re-issue it if the user agrees.
        // Cancel has to be set before the first await, or the shutdown runs on while the prompt is still up.
        if (_desktop?.MainWindow is MainWindow { NeedsClosePrompt: true } window)
        {
            e.Cancel = true;
            _ = ConfirmThenQuitAsync(window);
            return;
        }

        await TeardownAsync();
    }

    /// <summary>Runs the shell's close gate, then re-issues the quit the gate above cancelled. Backing out of
    /// either question simply leaves the editor running with the session untouched.</summary>
    private async Task ConfirmThenQuitAsync(MainWindow window)
    {
        try
        {
            if (!await window.ConfirmCloseAsync())
                return;

            window.ApproveClose(); // also clears NeedsClosePrompt, so neither gate asks again on the way out

            // Close the shell first, so its own teardown (timers, autosave, monitors — MainWindow.OnClosed)
            // runs before the engine underneath it goes away, exactly as it does when the user closes the
            // window directly. Under the default ShutdownMode.OnLastWindowClose that alone completes the quit
            // (re-entering this handler, which now sails past the gate); Shutdown() is the backstop, and is
            // needed on its own terms because it bypasses this event rather than raising it again.
            window.Close();
            await TeardownAsync();
            _desktop!.Shutdown();
        }
        catch (Exception ex)
        {
            // Nothing awaits this task. Log rather than let the failure vanish and strand the user in an
            // editor that silently refuses to quit.
            CrashLog.Write("Failed to quit", ex);
        }
    }

    /// <summary>Tears the live session down: the MCP server first (stop accepting AI edits before the model it
    /// edits goes away), then the proxy worker and the playback engine. Idempotent — the quit path runs it
    /// explicitly, because the programmatic <c>Shutdown()</c> it then calls bypasses this event.</summary>
    private async Task TeardownAsync()
    {
        if (_tornDown)
            return;
        _tornDown = true;

        if (_mcp is { } mcp)
            await mcp.DisposeAsync();
        _proxy?.Dispose();
        if (_engine is { } engine)
            await engine.DisposeAsync();
    }
}
