using System;
using System.IO;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.App;

/// <summary>
/// Imports a source file into a project's <see cref="MediaPool"/> (PLAN.md step 16b): probes it via
/// <see cref="MediaSource"/> for format/duration and adds a <see cref="MediaRef"/> through the command stack
/// (<see cref="AddMediaCommand"/>), so the import is undoable and flips the dirty indicator (step 10). This is
/// the one place the App's file-import UI touches the Media layer; the bin / thumbnail / badge path (step 15)
/// then lights up for the imported source.
/// </summary>
internal static class MediaImport
{
    /// <summary>
    /// The outcome of an import: the imported (or already-present) <see cref="MediaRef"/> on success, or a
    /// human-readable <see cref="Error"/> explaining why the file could not be opened.
    /// </summary>
    public readonly record struct Result(MediaRef? Media, string? Error)
    {
        public bool Succeeded => Media is not null;
    }

    /// <summary>The default on-timeline duration for a newly imported still, matching the convention used by
    /// other editors (PLAN.md step 42), used when
    /// no per-project preference is supplied (MCP / CLI import).</summary>
    public static readonly Timecode DefaultStillDuration = Timecode.FromSeconds(5);

    /// <summary>
    /// Probes <paramref name="path"/> and adds it to the project (deduplicating by absolute path — re-importing
    /// the same file returns the existing reference rather than a second copy). A recognised image file imports as
    /// a single <see cref="MediaKind.Still"/> with <paramref name="stillDuration"/> (defaulting to
    /// <see cref="DefaultStillDuration"/>) as its initial on-timeline length (PLAN.md step 42); everything else
    /// imports as an ordinary file. Returns a <see cref="Result"/> carrying the imported/existing
    /// <see cref="MediaRef"/>, or the failure reason (the underlying FFmpeg message) when the file can't be
    /// opened/probed — so the caller can show *why* rather than a bare "failed".
    /// </summary>
    public static Result TryImport(Project project, EditHistory history, string path, Timecode? stillDuration = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(path))
            return new Result(null, "No file path.");
        if (!File.Exists(path))
            return new Result(null, "File not found.");

        // Already imported? Match on the stored absolute path so a file isn't added twice.
        foreach (MediaRef existing in project.MediaPool.Items)
            if (string.Equals(existing.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
                return new Result(existing, null);

        bool isStill = ImageSequenceDetection.IsImagePath(path);
        ProbedMediaInfo info;
        try
        {
            // ProbeInfo (not Open) so audio-only sources — .m4a / .mp3 / .wav — import too (they have no
            // video stream for MediaSource's video decoder to open).
            info = MediaSource.ProbeInfo(path);
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message); // surfaced to the user (§15)
        }

        if (isStill)
            // The probed duration is one frame; a still's on-timeline length is the import preference, and its
            // media headroom is unbounded (MediaRef.HasUnboundedDuration) so it extends freely like a generator.
            info = info with { Duration = stillDuration ?? DefaultStillDuration };

        var media = new MediaRef(MediaRefId.New(), path, info)
        {
            Kind = isStill ? MediaKind.Still : MediaKind.File,
        };
        history.Execute(new AddMediaCommand(project.MediaPool, media));
        return new Result(media, null);
    }

    /// <summary>
    /// Imports a detected numbered image run as one <see cref="MediaKind.ImageSequence"/> clip at
    /// <paramref name="frameRate"/> (PLAN.md step 42), deduplicating by the sequence's printf pattern. The
    /// intrinsic frame rate and the total duration (<c>frameCount / fps</c>) are the single source of truth for the
    /// sequence's timing; the probe supplies the per-frame facts (dimensions, alpha, codec).
    /// </summary>
    public static Result TryImportSequence(Project project, EditHistory history, ImageSequenceInfo sequence, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        if (frameRate.Num <= 0)
            return new Result(null, "Invalid sequence frame rate.");

        // Already imported? Match on the pattern so the same run isn't added twice.
        foreach (MediaRef existing in project.MediaPool.Items)
            if (existing.Kind == MediaKind.ImageSequence
                && string.Equals(existing.SequencePattern, sequence.Pattern, StringComparison.OrdinalIgnoreCase))
                return new Result(existing, null);

        var media = new MediaRef(MediaRefId.New(), sequence.FirstFilePath, PlaceholderInfo(frameRate))
        {
            Kind = MediaKind.ImageSequence,
            SequencePattern = sequence.Pattern,
            SequenceStartNumber = sequence.StartNumber,
            SequenceFrameCount = sequence.FrameCount,
        };

        ProbedMediaInfo probed;
        try
        {
            probed = MediaSource.ProbeInfo(MediaOpenRequest.FromMediaRef(media));
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message);
        }

        // fps and duration are authoritative from the user's choice + frame count (exact Timecode math), not the
        // demuxer's report; keep the probed per-frame facts (size, alpha, pixel format, codec).
        media.Info = probed with
        {
            HasVideo = true,
            FrameRate = frameRate,
            Duration = Timecode.FromFrames(sequence.FrameCount, frameRate),
        };
        history.Execute(new AddMediaCommand(project.MediaPool, media));
        return new Result(media, null);
    }

    // A minimal info used only to carry the chosen frame rate into the image2 open request before the real probe.
    private static ProbedMediaInfo PlaceholderInfo(Rational frameRate) =>
        new(Duration: Timecode.Zero, HasVideo: true, FrameRate: frameRate, Width: 0, Height: 0,
            HasAudio: false, SampleRate: 0, Channels: 0);
}
