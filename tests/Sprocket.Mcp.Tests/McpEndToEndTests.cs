using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// End-to-end passes with the SDK's <b>real client</b> (PLAN.md step 38), exercising the full
/// serialization / schema / dispatch path the attribute-and-delegate tool tests can't:
/// (a) an in-memory stream transport (no ports — CI-safe), and (b) the actual
/// <see cref="McpServerHost"/> HttpListener endpoint on an ephemeral loopback port, including the
/// Origin (403) and bearer-token (401) rejections.
/// </summary>
public class McpEndToEndTests
{
    private static McpServerOptions BuildOptions(FakeEditorSession session)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Sprocket", Version = "0.0.0" },
            ToolCollection = [],
        };
        foreach (McpServerTool tool in new SprocketTools(session).BuildTools())
            options.ToolCollection!.Add(tool);
        return options;
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Full_Client_Session_Over_In_Memory_Streams()
    {
        var session = new FakeEditorSession();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "Sprocket");
        await using McpServer server = McpServer.Create(serverTransport, BuildOptions(session));
        using var serverCts = new CancellationTokenSource();
        Task serverRun = server.RunAsync(serverCts.Token);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream());
        await using McpClient client = await McpClient.CreateAsync(clientTransport);

        // The advertised tool list is the full surface, with schemas generated from the signatures.
        IList<McpClientTool> tools = await client.ListToolsAsync();
        Assert.Equal(new SprocketTools(session).BuildTools().Count, tools.Count);
        McpClientTool addMarker = tools.Single(t => t.Name == "add_marker");
        Assert.Contains("positionTicks", addMarker.JsonSchema.GetRawText());

        // Read → edit → undo round-trip against the live model.
        CallToolResult state = await client.CallToolAsync("get_project_state");
        JsonNode root = JsonNode.Parse(TextOf(state))!;
        Assert.Equal(240000, (long)root["ticks_per_second"]!);

        CallToolResult added = await client.CallToolAsync("add_marker",
            new Dictionary<string, object?> { ["positionTicks"] = 120000L, ["name"] = "beat" });
        Assert.NotEqual(true, added.IsError);
        Assert.Equal("beat", session.Project.Timeline.Markers.Single().Name);

        CallToolResult undone = await client.CallToolAsync("undo");
        Assert.NotEqual(true, undone.IsError);
        Assert.Empty(session.Project.Timeline.Markers);

        // A tool failure comes back as a tool error, not a protocol fault.
        CallToolResult bad = await client.CallToolAsync("delete_clip",
            new Dictionary<string, object?> { ["clipId"] = 99999 });
        Assert.True(bad.IsError);
        Assert.Contains("list_clips", TextOf(bad));

        serverCts.Cancel();
        try { await serverRun; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task HttpListener_Host_Serves_A_Real_Client_And_Enforces_Auth()
    {
        var session = new FakeEditorSession();
        int port = FreePort();

        // No token: the SDK client connects and drives an edit through the real HTTP endpoint.
        await using (var host = new McpServerHost(port, bearerToken: null, () => session))
        {
            host.Start();
            await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(host.Url) }));
            IList<McpClientTool> tools = await client.ListToolsAsync();
            Assert.Equal(new SprocketTools(session).BuildTools().Count, tools.Count);

            CallToolResult added = await client.CallToolAsync("add_marker",
                new Dictionary<string, object?> { ["positionTicks"] = 240000L, ["name"] = "http" });
            Assert.NotEqual(true, added.IsError);
            Assert.Equal("http", session.Project.Timeline.Markers.Single().Name);

            // A browser-style request (Origin header) is rejected outright.
            using var http = new HttpClient();
            using var browserRequest = new HttpRequestMessage(HttpMethod.Post, host.Url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            browserRequest.Headers.Add("Origin", "https://evil.example");
            using HttpResponseMessage forbidden = await http.SendAsync(browserRequest);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            // GET has no standalone stream in stateless mode.
            using HttpResponseMessage get = await http.GetAsync(host.Url);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        }

        // Token required: a missing/wrong Authorization header gets 401; the right one works.
        int tokenPort = FreePort();
        await using (var host = new McpServerHost(tokenPort, bearerToken: "s3cret", () => session))
        {
            host.Start();
            using var http = new HttpClient();
            using HttpResponseMessage unauthorized = await http.PostAsync(host.Url,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.Url),
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" },
                }));
            Assert.Equal(new SprocketTools(new FakeEditorSession()).BuildTools().Count, (await client.ListToolsAsync()).Count);
        }
    }

    [Fact]
    public async Task Host_Without_A_Session_Returns_503_Until_One_Attaches()
    {
        IEditorSession? current = null;
        int port = FreePort();
        await using var host = new McpServerHost(port, bearerToken: null, () => current);
        host.Start();

        using var http = new HttpClient();
        using HttpResponseMessage parked = await http.PostAsync(host.Url,
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, parked.StatusCode);

        current = new FakeEditorSession(); // session swap (File > New) — same host serves the new session
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(host.Url) }));
        Assert.Equal(new SprocketTools(new FakeEditorSession()).BuildTools().Count, (await client.ListToolsAsync()).Count);
    }

    /// <summary>An OS-assigned free TCP port (bind :0, read it back, release).</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
