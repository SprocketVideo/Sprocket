using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sprocket.Mcp;

namespace Sprocket.App;

/// <summary>
/// The <c>--mcp-check</c> headless smoke test: starts the real <see cref="McpServerHost"/> on a loopback
/// port and drives one genuine JSON-RPC exchange over it (<c>initialize</c> then <c>tools/list</c>),
/// proving the packaged MCP endpoint binds, accepts a POST, builds its tool collection, and responds —
/// the one thing the FFmpeg/audio smokes don't cover. Nothing here touches Avalonia or a project model:
/// the exchange stops at listing tools, so the stub session is never actually invoked.
/// </summary>
internal static class McpCheck
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        Console.WriteLine("== Sprocket mcp-check ==");

        // Bind an unprivileged loopback port; retry a small range so a busy port doesn't fail the smoke.
        McpServerHost? host = null;
        int port = 0;
        foreach (int candidate in Enumerable.Range(47800, 12))
        {
            try
            {
                var h = new McpServerHost(candidate, bearerToken: null, session: () => StubSession.Instance);
                h.Start();
                host = h;
                port = candidate;
                break;
            }
            catch (McpStartException)
            {
                // Port in use — try the next one.
            }
        }

        if (host is null)
        {
            Console.Error.WriteLine("[mcp-check] FAIL: could not bind any loopback port in 47800-47811.");
            return 1;
        }

        try
        {
            string url = host.Url;
            Console.WriteLine($"endpoint: {url}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            // 1) initialize — the MCP handshake.
            JsonElement init = await PostAsync(client, url,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"mcp-check","version":"1.0"}}}""");
            if (!init.TryGetProperty("result", out JsonElement initResult) ||
                !initResult.TryGetProperty("serverInfo", out JsonElement serverInfo))
            {
                Console.Error.WriteLine($"[mcp-check] FAIL: initialize did not return a serverInfo result: {init}");
                return 1;
            }
            string serverName = serverInfo.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "?" : "?";
            Console.WriteLine($"[ok]   initialize -> serverInfo.name = {serverName}");

            // 2) tools/list — proves the tool collection built and dispatch works end-to-end.
            JsonElement list = await PostAsync(client, url,
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            if (!list.TryGetProperty("result", out JsonElement listResult) ||
                !listResult.TryGetProperty("tools", out JsonElement tools) ||
                tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() == 0)
            {
                Console.Error.WriteLine($"[mcp-check] FAIL: tools/list returned no tools: {list}");
                return 1;
            }
            Console.WriteLine($"[ok]   tools/list -> {tools.GetArrayLength()} tools");

            Console.WriteLine("[mcp-check] RESULT: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp-check] FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    // POST one JSON-RPC message and return the parsed JSON-RPC response. The host answers in
    // Streamable-HTTP style (an SSE body carrying the result in a `data:` line), so unwrap that.
    private static async Task<JsonElement> PostAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Accept", "application/json, text/event-stream");

        using HttpResponseMessage resp = await client.SendAsync(request);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

        // Concatenate the SSE `data:` payload(s); fall back to the raw body if it was plain JSON.
        var sb = new StringBuilder();
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                sb.Append(trimmed["data:".Length..].Trim());
        }
        string payload = sb.Length > 0 ? sb.ToString() : body.Trim();
        if (payload.Length == 0)
            throw new InvalidOperationException("empty response body");

        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    /// <summary>A non-null session so the host's per-request session guard passes. Its method is never
    /// reached by <c>initialize</c>/<c>tools/list</c> (no tool is invoked).</summary>
    private sealed class StubSession : IEditorSession
    {
        public static readonly StubSession Instance = new();
        public Task<T> OnModelThreadAsync<T>(Func<IEditorApi, T> fn) =>
            throw new NotSupportedException("--mcp-check does not execute tools");
    }
}
