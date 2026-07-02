using System;
using System.Threading.Tasks;
using Sprocket.Mcp;

namespace Sprocket.App;

/// <summary>
/// The app-scoped controller for the loopback MCP server (PLAN.md step 38). Lives on <see cref="App"/> —
/// not the window — because File ▸ New/Open rebuilds the whole <c>MainWindow</c>: the listener survives the
/// swap and only its <see cref="IEditorSession"/> slot is re-pointed at the new session. Started/stopped
/// purely by the user-settings toggle (honouring the persisted flag at launch is that toggle's state —
/// never auto-started otherwise); a port or token change restarts the listener.
/// </summary>
internal sealed class McpServerService : IAsyncDisposable
{
    /// <summary>The listener's lifecycle state, driving the status-bar indicator.</summary>
    public enum McpState
    {
        /// <summary>Not listening (the settings toggle is off).</summary>
        Off,

        /// <summary>Listening on <see cref="Port"/>.</summary>
        Listening,

        /// <summary>The toggle is on but the listener failed to start (see <see cref="LastError"/>).</summary>
        Error,
    }

    private McpServerHost? _host;
    private volatile IEditorSession? _session;
    private (bool Enabled, int Port, string? Token) _applied;

    public McpState State { get; private set; } = McpState.Off;

    /// <summary>The port in effect while <see cref="State"/> is <see cref="McpState.Listening"/>.</summary>
    public int Port { get; private set; }

    /// <summary>The bind failure message while <see cref="State"/> is <see cref="McpState.Error"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised (on the calling/UI thread) after <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Points the running server at a (new) window session; <see langword="null"/> parks it
    /// (requests get 503s) during a swap.</summary>
    public void AttachSession(IEditorSession? session) => _session = session;

    /// <summary>Applies the MCP fields of <paramref name="settings"/>: starts, stops, or restarts the
    /// listener as needed. Call on the UI thread.</summary>
    public async Task ApplyAsync(UserSettings settings)
    {
        string? token = settings.McpRequireToken && !string.IsNullOrEmpty(settings.McpToken)
            ? settings.McpToken
            : null;
        (bool Enabled, int Port, string? Token) wanted = (settings.McpEnabled, settings.McpPort, token);
        if (wanted == _applied && State != McpState.Error)
            return; // nothing to change (and no failed start to retry)
        _applied = wanted;

        if (_host is { } old)
        {
            _host = null;
            await old.DisposeAsync();
        }

        if (!wanted.Enabled)
        {
            SetState(McpState.Off, 0, null);
            return;
        }

        var host = new McpServerHost(wanted.Port, wanted.Token, () => _session);
        try
        {
            host.Start();
        }
        catch (McpStartException ex)
        {
            await host.DisposeAsync();
            SetState(McpState.Error, wanted.Port, ex.Message);
            return;
        }
        _host = host;
        SetState(McpState.Listening, wanted.Port, null);
    }

    private void SetState(McpState state, int port, string? error)
    {
        State = state;
        Port = port;
        LastError = error;
        StateChanged?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync();
        }
    }
}
