using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Timing;

namespace Sprocket.Mcp;

/// <summary>
/// Session tools (PLAN.md step 38 follow-on): project lifecycle (open / new-or-close / save as), video
/// export with progress polling, and the fuller transport surface (stop, go to start/end, frame stepping).
/// </summary>
public sealed partial class SprocketTools
{
    // ── Project lifecycle ───────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "open_project")]
    [Description("Opens a project file (absolute path to a .sprocket.json), replacing the current editing " +
                 "session. Fails when the current project has unsaved changes unless discardChanges=true — " +
                 "save first (save_project / save_project_as) to keep them. The editor swaps sessions in the " +
                 "background: re-read state with get_project_state before further edits.")]
    public Task<string> OpenProject(
        [Description("Absolute path of the project file on this machine.")] string path,
        [Description("Discard unsaved changes in the current project (default false).")] bool discardChanges = false) =>
        _session.OnModelThreadAsync(api =>
        {
            if (api.IsDirty && !discardChanges)
                throw new McpException("the current project has unsaved changes — save_project first, or pass " +
                                       "discardChanges=true to drop them.");
            McpResult<bool> result = api.OpenProject(path);
            if (!result.Ok)
                throw new McpException(result.Error ?? "open failed.");
            return new JsonObject
            {
                ["opened"] = path,
                ["note"] = "The editor is swapping sessions; call get_project_state to see the opened project.",
            }.ToJsonString();
        });

    [McpServerTool(Name = "close_project")]
    [Description("Closes the current project by swapping to a fresh empty untitled one (the editor always " +
                 "has a project open — this is File > New). Fails on unsaved changes unless " +
                 "discardChanges=true.")]
    public Task<string> CloseProject(
        [Description("Discard unsaved changes in the current project (default false).")] bool discardChanges = false) =>
        NewProjectCore(discardChanges, "closed the project (a fresh empty project is now open)");

    [McpServerTool(Name = "new_project")]
    [Description("Starts a fresh empty untitled project (one video + one audio track), replacing the current " +
                 "session. Fails on unsaved changes unless discardChanges=true.")]
    public Task<string> NewProject(
        [Description("Discard unsaved changes in the current project (default false).")] bool discardChanges = false) =>
        NewProjectCore(discardChanges, "started a new empty project");

    private Task<string> NewProjectCore(bool discardChanges, string note) =>
        _session.OnModelThreadAsync(api =>
        {
            if (api.IsDirty && !discardChanges)
                throw new McpException("the current project has unsaved changes — save_project first, or pass " +
                                       "discardChanges=true to drop them.");
            McpResult<bool> result = api.NewProject();
            if (!result.Ok)
                throw new McpException(result.Error ?? "could not start a new project.");
            return new JsonObject
            {
                ["note"] = note + "; the editor is swapping sessions — call get_project_state to continue.",
            }.ToJsonString();
        });

    [McpServerTool(Name = "save_project_as")]
    [Description("Saves the project to a new file (absolute path; conventionally *.sprocket.json) and " +
                 "re-points the document at it — File > Save As. Use save_project for an already-titled " +
                 "project.")]
    public Task<string> SaveProjectAs(
        [Description("Absolute destination path for the project file.")] string path) =>
        _session.OnModelThreadAsync(api =>
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new McpException("pass an absolute destination path.");
            if (!api.SaveProjectAs(path))
                throw new McpException($"could not save to '{path}' — check the directory exists and is writable.");
            return StateFormatter.HistoryState(api.History, $"saved as: {path}");
        });

    // ── Export ──────────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "export_video")]
    [Description("Starts exporting the active sequence to a video file (MP4 / H.264 + AAC — the app's default " +
                 "delivery format) on a background thread and returns immediately. Rate control: quality mode " +
                 "(the default) holds visual quality via a CRF value (1–51, lower = better; default 18 ≈ visually " +
                 "lossless, 23 good for web, 28 small file) and lets file size float; bitrate mode aims at a Mbps " +
                 "target (optionally capped by maxBitrateMbps) for predictable size. Poll get_export_status for " +
                 "progress; cancel with cancel_export. Playback is suspended while the export runs. Only one " +
                 "export can run at a time.")]
    public Task<string> ExportVideo(
        [Description("Absolute output path; use the .mp4 extension.")] string outputPath,
        [Description("Skip the audio stream entirely (default false).")] bool videoOnly = false,
        [Description("Export only from this tick (inclusive); omit for the whole timeline.")] long? rangeInTicks = null,
        [Description("Export only up to this tick (exclusive); omit for the whole timeline.")] long? rangeOutTicks = null,
        [Description("Rate control mode: \"quality\" (constant quality, the default) or \"bitrate\" (target bit rate).")] string? rateControl = null,
        [Description("Constant-quality CRF 1–51 for quality mode (lower = better); omit for the default (18).")] int? crf = null,
        [Description("Target bit rate in Mbps for bitrate mode; omit for a resolution-scaled default (≈40 for 4K, 16 for 1080p).")] double? bitrateMbps = null,
        [Description("Optional VBR ceiling in Mbps for bitrate mode; must be ≥ bitrateMbps.")] double? maxBitrateMbps = null,
        [Description("Encode on the GPU when available, falling back to software (default false = deterministic software).")] bool hardware = false) =>
        _session.OnModelThreadAsync(api =>
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathRooted(outputPath))
                throw new McpException("pass an absolute output path.");
            if (rangeInTicks is { } rin && rangeOutTicks is { } rout && rout <= rin)
                throw new McpException("rangeOutTicks must be after rangeInTicks.");
            bool bitrateMode = string.Equals(rateControl?.Trim(), "bitrate", StringComparison.OrdinalIgnoreCase);
            if (rateControl is not null && !bitrateMode
                && !string.Equals(rateControl.Trim(), "quality", StringComparison.OrdinalIgnoreCase))
                throw new McpException($"unknown rateControl '{rateControl}' — use quality or bitrate.");
            if (crf is { } c && (c < 1 || c > 51))
                throw new McpException("crf must be between 1 and 51 (lower = better quality).");
            if (crf is not null && bitrateMode)
                throw new McpException("crf applies to quality mode — omit it, or use rateControl: quality.");
            if (bitrateMbps is <= 0 || maxBitrateMbps is <= 0)
                throw new McpException("bitrate values must be positive Mbps figures.");
            if (bitrateMbps is not null && !bitrateMode)
                throw new McpException("bitrateMbps applies to bitrate mode — pass rateControl: bitrate.");
            if (maxBitrateMbps is { } max && bitrateMbps is { } target && max < target)
                throw new McpException("maxBitrateMbps must be ≥ bitrateMbps.");
            if (maxBitrateMbps is not null && !bitrateMode)
                throw new McpException("maxBitrateMbps applies to bitrate mode — pass rateControl: bitrate.");
            McpResult<bool> result = api.StartExport(
                outputPath, videoOnly, rangeInTicks, rangeOutTicks,
                bitrateMode ? "bitrate" : "quality", crf, bitrateMbps, maxBitrateMbps, hardware);
            if (!result.Ok)
                throw new McpException(result.Error ?? "export could not start.");
            return StateFormatter.ExportStatus(api.ExportStatus);
        });

    [McpServerTool(Name = "export_audio")]
    [Description("Starts an audio-only export of the active sequence's master mix (no video) on a background " +
                 "thread and returns immediately. format is one of wav / flac / mp3 / aac / opus; give outputPath " +
                 "the matching extension (.wav/.flac/.mp3/.m4a/.opus). Poll get_export_status for progress; cancel " +
                 "with cancel_export. Playback is suspended while the export runs. Only one export can run at a time.")]
    public Task<string> ExportAudio(
        [Description("Absolute output path; use the extension matching the format.")] string outputPath,
        [Description("Audio format: wav / flac / mp3 / aac / opus.")] string format = "wav",
        [Description("Export only from this tick (inclusive); omit for the whole timeline.")] long? rangeInTicks = null,
        [Description("Export only up to this tick (exclusive); omit for the whole timeline.")] long? rangeOutTicks = null) =>
        _session.OnModelThreadAsync(api =>
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathRooted(outputPath))
                throw new McpException("pass an absolute output path.");
            if (rangeInTicks is { } rin && rangeOutTicks is { } rout && rout <= rin)
                throw new McpException("rangeOutTicks must be after rangeInTicks.");
            McpResult<bool> result = api.StartAudioExport(outputPath, format, rangeInTicks, rangeOutTicks);
            if (!result.Ok)
                throw new McpException(result.Error ?? "export could not start.");
            return StateFormatter.ExportStatus(api.ExportStatus);
        });

    [McpServerTool(Name = "get_export_status", ReadOnly = true, Idempotent = true)]
    [Description("The progress/outcome of the export started by export_video / export_audio: running + progress " +
                 "in [0,1] while encoding, then completed / cancelled / error.")]
    public Task<string> GetExportStatus() =>
        _session.OnModelThreadAsync(api => StateFormatter.ExportStatus(api.ExportStatus));

    [McpServerTool(Name = "cancel_export")]
    [Description("Cancels the running export (the partial output file is deleted). A no-op when none is " +
                 "running.")]
    public Task<string> CancelExport() => _session.OnModelThreadAsync(api =>
    {
        api.CancelExport();
        return StateFormatter.ExportStatus(api.ExportStatus);
    });

    // ── Extended transport ──────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "stop", Idempotent = true)]
    [Description("Stops playback, parking the playhead where it is (the NLE stop — same transport state as " +
                 "pause). Use go_to_start to rewind.")]
    public Task<string> Stop() => _session.OnModelThreadAsync(api =>
    {
        api.Pause();
        return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
    });

    [McpServerTool(Name = "go_to_start", Idempotent = true)]
    [Description("Rewinds the playhead to the beginning of the sequence (playback keeps its current " +
                 "playing/paused state).")]
    public Task<string> GoToStart() => _session.OnModelThreadAsync(api =>
    {
        api.Seek(0);
        return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
    });

    [McpServerTool(Name = "go_to_end", Idempotent = true)]
    [Description("Moves the playhead to the end of the sequence.")]
    public Task<string> GoToEnd() => _session.OnModelThreadAsync(api =>
    {
        api.Seek(api.DurationTicks);
        return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
    });

    [McpServerTool(Name = "step_frames", Idempotent = true)]
    [Description("Steps the playhead by whole frames (negative = backwards), pausing playback first — the " +
                 "arrow-key frame step.")]
    public Task<string> StepFrames(
        [Description("How many frames to step (e.g. 1, -1, 10).")] int frames = 1) =>
        _session.OnModelThreadAsync(api =>
        {
            if (frames == 0)
                throw new McpException("frames must be non-zero.");
            Rational fps = api.Project.Timeline.FrameRate;
            long frameTicks = fps.Num > 0 ? Timecode.FromFrames(1, fps).Ticks : 0;
            if (frameTicks <= 0)
                throw new McpException("the sequence has no valid frame rate.");
            api.Pause();
            long currentFrame = api.PlayheadTicks / frameTicks;
            long target = Math.Clamp((currentFrame + frames) * frameTicks, 0, api.DurationTicks);
            api.Seek(target);
            return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
        });
}
