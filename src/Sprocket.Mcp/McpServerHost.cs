using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sprocket.Mcp;

/// <summary>Thrown when the MCP listener cannot bind its port (in use / no permission); the settings UI
/// surfaces the message.</summary>
public sealed class McpStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The loopback MCP endpoint (PLAN.md step 38): a plain <see cref="HttpListener"/> bound to
/// <c>http://127.0.0.1:{port}/mcp/</c> fronting the official SDK's <see cref="StreamableHttpServerTransport"/>
/// in <b>stateless</b> Streamable-HTTP mode — each POST is an independent JSON-RPC exchange, served by a
/// per-request <see cref="McpServer"/> over the shared tool collection. Deliberately no ASP.NET Core /
/// Kestrel in a desktop app; the per-request sequence mirrors the SDK's own
/// <c>ModelContextProtocol.AspNetCore/StreamableHttpHandler</c> stateless path (the drop-in fallback if
/// this plumbing ever proves limiting).
///
/// Security posture: the loopback-only prefix is the primary control (no remote exposure, and no admin
/// URLACL needed on Windows); any request carrying an <c>Origin</c> header is rejected (browsers always
/// send one — kills drive-by-web DNS-rebinding POSTs); an optional bearer token additionally shuts out
/// other local processes. Started only by the user's settings toggle — never auto-started.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly int _port;
    private readonly string? _bearerToken;
    private readonly Func<IEditorSession?> _session;
    private readonly IReadOnlyList<McpServerTool> _tools;
    private readonly CancellationTokenSource _shutdown = new();
    private HttpListener? _listener;
    private Task? _acceptLoop;

    /// <summary>Creates a host for <paramref name="port"/>. A null/empty <paramref name="bearerToken"/>
    /// disables token auth (loopback-only). <paramref name="session"/> resolves the live editor session per
    /// call — <see langword="null"/> (no window yet / mid session-swap) yields 503s rather than failures.</summary>
    public McpServerHost(int port, string? bearerToken, Func<IEditorSession?> session)
    {
        _port = port;
        _bearerToken = string.IsNullOrEmpty(bearerToken) ? null : bearerToken;
        _session = session;
        // One tool set for the host's lifetime, bound to the live session through a swap-safe proxy.
        _tools = new SprocketTools(new CurrentSession(session)).BuildTools();
    }

    /// <summary>The endpoint URL clients connect to.</summary>
    public string Url => $"http://127.0.0.1:{_port}/mcp";

    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    private static readonly string ServerVersion =
        typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Binds the port and starts accepting. Throws <see cref="McpStartException"/> when the port
    /// can't be bound (already in use, permissions).</summary>
    public void Start()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/mcp/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new McpStartException($"MCP server could not listen on port {_port}: {ex.Message}", ex);
        }
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _shutdown.Token);
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // listener stopped — normal shutdown
            }
            _ = HandleRequestSafeAsync(context, ct);
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleRequestAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Client went away mid-response or we're shutting down — nothing to report.
        }
        catch (Exception)
        {
            TryRespondError(context, "Internal error.", 500);
        }
        finally
        {
            try { context.Response.Close(); }
            catch { /* already closed / aborted */ }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        // DNS-rebinding / drive-by-browser guard: a browser always attaches Origin to cross-origin
        // requests; no legitimate MCP client does. Reject outright per the MCP transport security guidance.
        if (request.Headers["Origin"] is not null)
        {
            TryRespondError(context, "Forbidden: browser-originated requests are not accepted.", 403);
            return;
        }

        if (_bearerToken is { } token &&
            !string.Equals(request.Headers["Authorization"], $"Bearer {token}", StringComparison.Ordinal))
        {
            TryRespondError(context, "Unauthorized: this server requires its bearer token " +
                "(see Sprocket's Preferences > AI control).", 401);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            // Stateless mode has no standalone SSE stream (GET) and no session to DELETE.
            response.StatusCode = 405;
            response.Headers["Allow"] = "POST";
            return;
        }

        JsonRpcMessage? message;
        try
        {
            message = await JsonSerializer.DeserializeAsync(
                request.InputStream, MessageTypeInfo, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            message = null;
        }
        if (message is null)
        {
            TryRespondError(context, "Bad Request: the POST body did not contain a valid JSON-RPC message.", 400);
            return;
        }

        if (_session() is null)
        {
            TryRespondError(context, "No project session is available (the editor is starting or switching projects) — retry shortly.", 503);
            return;
        }

        // One independent stateless exchange: fresh transport + server over the shared tool collection,
        // both torn down when the response completes (the SDK AspNetCore handler's stateless sequence).
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "Sprocket",
                Title = "Sprocket video editor",
                Version = ServerVersion,
            },
            ToolCollection = [],
        };
        foreach (McpServerTool tool in _tools)
            options.ToolCollection!.Add(tool);

        await using var transport = new StreamableHttpServerTransport { Stateless = true };
        await using McpServer server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
        Task run = server.RunAsync(ct);
        bool wrote;
        try
        {
            // The transport writes SSE events (or nothing) to the response body.
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache,no-store";
            response.SendChunked = true;
            wrote = await transport.HandlePostRequestAsync(message, response.OutputStream, ct).ConfigureAwait(false);
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            try { await run.ConfigureAwait(false); }
            catch { /* session pump teardown — the response already carries the outcome */ }
        }

        if (!wrote)
            response.StatusCode = 202; // a notification — nothing to send back
    }

    private static void TryRespondError(HttpListenerContext context, string message, int statusCode)
    {
        try
        {
            HttpListenerResponse response = context.Response;
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32000, message },
            }));
            response.OutputStream.Write(body);
        }
        catch
        {
            // The response may already be committed or the client gone; either way there is nothing better to do.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { _listener?.Stop(); }
        catch { /* already stopped */ }
        if (_acceptLoop is { } loop)
            await loop.ConfigureAwait(false);
        _listener?.Close();
        _shutdown.Dispose();
    }

    /// <summary>Delegates to whichever editor session is currently attached, so File ▸ New/Open (which
    /// rebuilds the window and its session) never invalidates the host or its tool collection.</summary>
    private sealed class CurrentSession(Func<IEditorSession?> resolve) : IEditorSession
    {
        public Task<T> OnModelThreadAsync<T>(Func<IEditorApi, T> fn) =>
            resolve() is { } session
                ? session.OnModelThreadAsync(fn)
                : throw new McpException("no project session is available — retry shortly.");
    }
}
