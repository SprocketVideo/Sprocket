using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Model;

namespace Sprocket.Mcp;

/// <summary>
/// Stabilization MCP tools (plan/features/stabilization.md phase 6): read a stabilized clip's analysis status and
/// kick off (or adopt a cached) background analysis. Analysis is a background, per-user-cached artifact — not a
/// model edit — so these do not go through <see cref="IEditorApi.History"/> and nothing is undoable.
/// </summary>
public sealed partial class SprocketTools
{
    [McpServerTool(Name = "stabilization_status", ReadOnly = true, Idempotent = true)]
    [Description("The stabilization analysis status of a clip: whether it has a Stabilization effect and, if so, " +
                 "the recovered motion track's state (not_analyzed / queued / analyzing / ready / failed), progress " +
                 "(0–1), and how many frames tracked at low confidence. Analysis is a cached background artifact, " +
                 "so it is not part of the project state.")]
    public Task<string> StabilizationStatus(
        [Description("clip_id from list_clips / get_project_state.")] int clipId) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            McpStabilizationInfo info = api.StabilizationInfoForClip(clip);
            if (!info.HasStabilization)
                throw new McpException($"clip {clipId} has no enabled Stabilization effect — add builtin.stabilization first.");
            return StabilizationJson(clipId, info).ToJsonString();
        });

    [McpServerTool(Name = "stabilization_analyze")]
    [Description("Starts (or adopts a cached) background motion analysis for a stabilized clip's source range, " +
                 "returning immediately — poll stabilization_status for progress. The clip must already carry a " +
                 "Stabilization effect (add_effect builtin.stabilization) and be backed by source media.")]
    public Task<string> StabilizationAnalyze(
        [Description("clip_id from list_clips / get_project_state.")] int clipId) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            McpResult<bool> result = api.AnalyzeStabilizationForClip(clip);
            if (!result.Ok)
                throw new McpException(result.Error ?? "could not start analysis.");
            return StabilizationJson(clipId, api.StabilizationInfoForClip(clip)).ToJsonString();
        });

    private static JsonObject StabilizationJson(int clipId, McpStabilizationInfo info) => new()
    {
        ["clip_id"] = clipId,
        ["detailed"] = info.Detailed,
        ["state"] = info.State,
        ["progress"] = info.Progress,
        ["low_confidence_frames"] = info.LowConfidenceFrames,
    };
}
