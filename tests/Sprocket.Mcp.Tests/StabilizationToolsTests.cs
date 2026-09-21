using System.Text.Json.Nodes;
using ModelContextProtocol;
using Sprocket.Core.Model;
using Sprocket.Mcp;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// The stabilization MCP tools (plan/features/stabilization.md phase 6): status reporting and starting analysis,
/// over the same fake session harness the other tool tests use (the fake's analysis completes instantly).
/// </summary>
public class StabilizationToolsTests
{
    private static async Task<(FakeEditorSession Session, SprocketTools Tools, int ClipId)> StabilizedClip()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;
        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline((string)imported["media_id"]!, 0, stream: "video"))!;
        int clipId = (int)placed["clip_id"]!;
        await tools.AddEffect(clipId, EffectTypeIds.Stabilization);
        return (session, tools, clipId);
    }

    [Fact]
    public async Task Status_reports_not_analyzed_then_analyze_makes_it_ready()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId) = await StabilizedClip();

        JsonNode before = JsonNode.Parse(await tools.StabilizationStatus(clipId))!;
        Assert.Equal("not_analyzed", (string)before["state"]!);
        Assert.Equal(clipId, (int)before["clip_id"]!);

        JsonNode analyzed = JsonNode.Parse(await tools.StabilizationAnalyze(clipId))!;
        Assert.Equal("ready", (string)analyzed["state"]!);
        Assert.Equal(1, session.StabilizationAnalyzeCount);

        JsonNode after = JsonNode.Parse(await tools.StabilizationStatus(clipId))!;
        Assert.Equal("ready", (string)after["state"]!);
    }

    [Fact]
    public async Task Status_on_a_clip_without_stabilization_is_an_error()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;
        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline((string)imported["media_id"]!, 0, stream: "video"))!;
        int clipId = (int)placed["clip_id"]!;

        await Assert.ThrowsAsync<McpException>(() => tools.StabilizationStatus(clipId));
    }

    [Fact]
    public async Task Analyze_on_an_unknown_clip_is_an_error()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        await Assert.ThrowsAsync<McpException>(() => tools.StabilizationAnalyze(9999));
    }
}
