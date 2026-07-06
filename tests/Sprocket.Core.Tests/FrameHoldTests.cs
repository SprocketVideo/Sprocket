using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Frame hold + stop-motion frame edits (PLAN.md step 43): a held clip's constant time map and independent
/// duration, the hold-ignores-speed precedence, hold-aware blade splits, <see cref="SetClipHoldCommand"/> /
/// <see cref="TrimHeldClipCommand"/> apply/revert/coalesce, and the <see cref="FrameHoldEdits"/> composites
/// (Add Frame Hold, Insert Frame Hold Segment, Duplicate Frame, Remove Frame) with exact frame-grid math.
/// </summary>
public class FrameHoldTests
{
    // Source span [0, 10s) placed at t = 4s (matching RetimeTests' baseline clip).
    private static Clip ClipAt4s() =>
        new(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.FromSeconds(4));

    private static Clip Hold(Clip clip, double atSeconds, double durationSeconds)
    {
        clip.HoldDuration = Timecode.FromSeconds(durationSeconds);
        clip.HoldFrameAt = Timecode.FromSeconds(atSeconds);
        return clip;
    }

    // ── Model semantics ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Held_Clip_Has_Constant_Source_Map_And_Independent_Duration()
    {
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 3);

        Assert.True(clip.IsHeld);
        Assert.Equal(Timecode.FromSeconds(3), clip.Duration);      // HoldDuration, not SourceOut - SourceIn
        Assert.Equal(Timecode.FromSeconds(7), clip.TimelineEnd);
        // The map is constant across the whole span (and beyond — it is a constant function).
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(4)));
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(5.5)));
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(7)));
    }

    [Fact]
    public void Hold_Ignores_Speed_Ratio()
    {
        Clip clip = ClipAt4s();
        clip.SpeedRatio = new Rational(2, 1); // derived duration would be 5s
        Hold(clip, atSeconds: 1, durationSeconds: 8);

        Assert.Equal(Timecode.FromSeconds(8), clip.Duration);       // hold wins over speed
        Assert.Equal(Timecode.FromSeconds(1), clip.MapToSource(Timecode.FromSeconds(11)));
        Assert.Equal(new Rational(2, 1), clip.SpeedRatio);           // retained untouched for un-hold
    }

    [Fact]
    public void Unhold_Restores_The_Derived_Duration_Exactly()
    {
        Clip clip = ClipAt4s();
        clip.SpeedRatio = new Rational(2, 1);
        Hold(clip, atSeconds: 2, durationSeconds: 42);

        clip.HoldFrameAt = null;

        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);       // (10s span) / 2× again
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(Timecode.FromSeconds(5))); // 1s in → 2s of source
    }

    [Fact]
    public void Clone_Copies_The_Hold()
    {
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 3);
        Clip copy = clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, Timecode.FromSeconds(20));

        Assert.Equal(clip.HoldFrameAt, copy.HoldFrameAt);
        Assert.Equal(clip.HoldDuration, copy.HoldDuration);
    }

    [Fact]
    public void Split_Partitions_A_Held_Clip_By_Timeline_Span_And_Keeps_The_Source_Span()
    {
        var track = new VideoTrack();
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 6); // timeline [4, 10)
        track.Clips.Add(clip);
        var history = new EditHistory();

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(6));
        history.Execute(split);

        // Both halves are frozen on the same frame and their spans partition the original.
        Assert.Equal(Timecode.FromSeconds(2), clip.HoldFrameAt!.Value);
        Assert.Equal(Timecode.FromSeconds(2), split.RightClip.HoldFrameAt!.Value);
        Assert.Equal(Timecode.FromSeconds(2), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(6), split.RightClip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(4), split.RightClip.Duration);
        // The retained source span is untouched on both halves (exact un-hold on either side).
        Assert.Equal(Timecode.Zero, clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(10), clip.SourceOut);
        Assert.Equal(Timecode.Zero, split.RightClip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(10), split.RightClip.SourceOut);

        history.Undo();
        Assert.Single(track.Clips);
        Assert.Equal(Timecode.FromSeconds(6), clip.HoldDuration);
        Assert.Equal(Timecode.FromSeconds(10), clip.SourceOut);
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetClipHoldCommand_Applies_Reverts_And_Coalesces()
    {
        Clip clip = ClipAt4s();
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetClipHoldCommand(clip, Timecode.FromSeconds(1), Timecode.FromSeconds(2)));
            history.Execute(new SetClipHoldCommand(clip, Timecode.FromSeconds(3), Timecode.FromSeconds(5)));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(3), clip.HoldFrameAt!.Value);
        Assert.Equal(Timecode.FromSeconds(5), clip.HoldDuration);

        history.Undo();
        Assert.False(clip.IsHeld);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void SetClipHoldCommand_Unhold_Keeps_The_Old_Duration_For_Redo_Symmetry()
    {
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 3);
        var history = new EditHistory();

        history.Execute(new SetClipHoldCommand(clip, null, default));
        Assert.False(clip.IsHeld);

        history.Undo();
        Assert.True(clip.IsHeld);
        Assert.Equal(Timecode.FromSeconds(2), clip.HoldFrameAt!.Value);
        Assert.Equal(Timecode.FromSeconds(3), clip.HoldDuration);
    }

    [Fact]
    public void TrimHeldClipCommand_Has_No_Media_Clamp_And_Coalesces()
    {
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 3);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            // Extend far past the 10s media length — a frozen frame has unbounded headroom.
            history.Execute(new TrimHeldClipCommand(clip, Timecode.FromSeconds(4), Timecode.FromSeconds(60)));
            history.Execute(new TrimHeldClipCommand(clip, Timecode.FromSeconds(5), Timecode.FromSeconds(90)));
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(Timecode.FromSeconds(5), clip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(90), clip.Duration);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(4), clip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(3), clip.HoldDuration);
    }

    // ── FrameHoldEdits composites ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AddFrameHold_Splits_And_Freezes_The_Right_Half_Without_Ripple()
    {
        var track = new VideoTrack();
        Clip clip = ClipAt4s(); // timeline [4, 14)
        var after = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(14));
        track.Clips.Add(clip);
        track.Clips.Add(after);
        var history = new EditHistory();

        (IEditCommand command, Clip held) = FrameHoldEdits.AddFrameHold(track, clip, Timecode.FromSeconds(9));
        history.Execute(command);

        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);            // left plays to the playhead
        Assert.Equal(Timecode.FromSeconds(5), held.HoldFrameAt!.Value);  // frozen on the playhead's source frame
        Assert.Equal(Timecode.FromSeconds(9), held.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(14), held.TimelineEnd);        // overall span unchanged
        Assert.Equal(Timecode.FromSeconds(14), after.TimelineStart);     // nothing rippled

        history.Undo();
        Assert.Single(track.Clips, c => ReferenceEquals(c, clip));
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void InsertFrameHoldSegment_Splits_Inserts_And_Ripples_Downstream()
    {
        var track = new VideoTrack();
        Clip clip = ClipAt4s(); // timeline [4, 14)
        var after = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(14));
        track.Clips.Add(clip);
        track.Clips.Add(after);
        var history = new EditHistory();

        (IEditCommand command, Clip held) = FrameHoldEdits.InsertFrameHoldSegment(
            track, clip, Timecode.FromSeconds(9), Timecode.FromSeconds(2), [(after, after.TimelineStart)]);
        history.Execute(command);

        Assert.Equal(3 + 1, track.Clips.Count);                          // left + tail + freeze, plus `after`
        Assert.Equal(Timecode.FromSeconds(9), held.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(2), held.Duration);
        Assert.Equal(Timecode.FromSeconds(5), held.HoldFrameAt!.Value);
        // The split's right half and the downstream clip both shifted right by the freeze length.
        Clip tail = track.Clips.First(c => !ReferenceEquals(c, clip) && !ReferenceEquals(c, held) && !ReferenceEquals(c, after));
        Assert.Equal(Timecode.FromSeconds(11), tail.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(16), after.TimelineStart);

        history.Undo();
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(Timecode.FromSeconds(14), after.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void InsertFrameHoldSegment_At_The_Clip_Start_Shifts_The_Clip_Instead_Of_Splitting()
    {
        var track = new VideoTrack();
        Clip clip = ClipAt4s();
        track.Clips.Add(clip);
        var history = new EditHistory();

        (IEditCommand command, Clip held) = FrameHoldEdits.InsertFrameHoldSegment(
            track, clip, Timecode.FromSeconds(4), Timecode.FromSeconds(2), []);
        history.Execute(command);

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(Timecode.FromSeconds(4), held.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(6), clip.TimelineStart);       // whole clip pushed right, not split
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    // Duplicate Frame on the exact source-frame grid: at 12, 24, and NTSC 30000/1001 fps the inserted hold's
    // span and the downstream shift are exactly one source frame's timeline span.
    [Theory]
    [InlineData(12, 1)]
    [InlineData(24, 1)]
    [InlineData(30000, 1001)]
    public void DuplicateFrame_Inserts_A_One_Frame_Hold_And_Ripples_One_Frame(int fpsNum, int fpsDen)
    {
        var fps = new Rational(fpsNum, fpsDen);
        long frameTicks = Timecode.FromFrames(1, fps).Ticks;

        var track = new VideoTrack();
        Clip clip = ClipAt4s(); // timeline [4, 14), source [0, 10)
        var after = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(14));
        track.Clips.Add(clip);
        track.Clips.Add(after);
        var history = new EditHistory();

        // Playhead mid-frame: 5s into the timeline = source 1s = frame index fps·1 exactly; nudge inside it.
        var at = new Timecode(Timecode.FromSeconds(5).Ticks + frameTicks / 2);
        FrameHoldEdits.FrameSpan span = FrameHoldEdits.SourceFrameSpan(clip, at, fps);
        (IEditCommand command, Clip held) = FrameHoldEdits.DuplicateFrame(
            track, clip, at, fps, [(after, after.TimelineStart)]);
        history.Execute(command);

        Assert.Equal(span.SourceFrameStart, held.HoldFrameAt!.Value);      // frozen on the frame's exact start
        Assert.Equal(span.TimelineEnd, held.TimelineStart);                // inserted at the frame's end boundary
        Assert.Equal(frameTicks, held.Duration.Ticks);                     // exactly one source frame long
        Assert.Equal(Timecode.FromSeconds(14).Ticks + frameTicks, after.TimelineStart.Ticks);

        history.Undo();
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(Timecode.FromSeconds(14), after.TimelineStart);
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(24, 1)]
    [InlineData(30000, 1001)]
    public void RemoveFrame_Extracts_One_Frame_And_Ripple_Closes(int fpsNum, int fpsDen)
    {
        var fps = new Rational(fpsNum, fpsDen);
        long frameTicks = Timecode.FromFrames(1, fps).Ticks;

        var track = new VideoTrack();
        Clip clip = ClipAt4s(); // timeline [4, 14)
        var after = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(14));
        track.Clips.Add(clip);
        track.Clips.Add(after);
        var history = new EditHistory();

        var at = new Timecode(Timecode.FromSeconds(5).Ticks + frameTicks / 2);
        history.Execute(FrameHoldEdits.RemoveFrame(track, clip, at, fps, [(after, after.TimelineStart)]));

        // Left part + tail remain, contiguous, one frame shorter in total; downstream closed by one frame.
        Assert.Equal(3, track.Clips.Count);
        Clip tail = track.Clips.First(c => !ReferenceEquals(c, clip) && !ReferenceEquals(c, after));
        Assert.Equal(clip.TimelineEnd, tail.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(10).Ticks - frameTicks, clip.Duration.Ticks + tail.Duration.Ticks);
        Assert.Equal(Timecode.FromSeconds(14).Ticks - frameTicks, after.TimelineStart.Ticks);
        // The tail's source resumes exactly one frame after the left part ends (the frame is gone).
        Assert.Equal(frameTicks, (tail.SourceIn - clip.SourceOut).Ticks);

        history.Undo();
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(Timecode.Zero, clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(10), clip.SourceOut);
        Assert.Equal(Timecode.FromSeconds(14), after.TimelineStart);
    }

    [Fact]
    public void RemoveFrame_On_A_Single_Frame_Clip_Removes_The_Clip()
    {
        var fps = new Rational(24, 1);
        long frameTicks = Timecode.FromFrames(1, fps).Ticks;

        var track = new VideoTrack();
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, new Timecode(frameTicks), Timecode.Zero);
        var after = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), new Timecode(frameTicks));
        track.Clips.Add(clip);
        track.Clips.Add(after);
        var history = new EditHistory();

        history.Execute(FrameHoldEdits.RemoveFrame(track, clip, Timecode.Zero, fps, [(after, after.TimelineStart)]));

        Assert.Single(track.Clips);
        Assert.Equal(Timecode.Zero, after.TimelineStart);

        history.Undo();
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(new Timecode(frameTicks), after.TimelineStart);
    }

    [Fact]
    public void SourceFrameSpan_Is_Exact_On_The_NTSC_Grid_Through_A_Speed_Ratio()
    {
        var fps = new Rational(30000, 1001); // 8008 ticks/frame exactly
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero)
        {
            SpeedRatio = new Rational(2, 1),
        };

        // Frame 100 spans source ticks [800800, 808808); at 2× that is timeline [400400, 404404).
        FrameHoldEdits.FrameSpan span = FrameHoldEdits.SourceFrameSpan(clip, new Timecode(400400), fps);
        Assert.Equal(800800, span.SourceFrameStart.Ticks);
        Assert.Equal(400400, span.TimelineStart.Ticks);
        Assert.Equal(404404, span.TimelineEnd.Ticks);
    }

    [Fact]
    public void RenderGraph_Plans_The_Identical_Source_Frame_Across_A_Held_Span()
    {
        // The same render graph serves preview and export, so a constant plan == the identical rendered frame:
        // every time in the held span plans the exact source time an unheld clip would plan at HoldFrameAt.
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var media = MediaRefId.New();
        var track = new VideoTrack();
        Clip clip = new(media, Timecode.Zero, Timecode.FromSeconds(10), Timecode.FromSeconds(4));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        Sprocket.Core.Rendering.VideoFramePlan unheld =
            Sprocket.Core.Rendering.RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(6));
        Timecode unheldSource = Assert.Single(unheld.Layers).SourceTime; // 2s

        Hold(clip, atSeconds: 2, durationSeconds: 10);
        foreach (double t in new[] { 4.0, 6.0, 9.5, 13.9 })
        {
            Sprocket.Core.Rendering.VideoFramePlan plan =
                Sprocket.Core.Rendering.RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(t));
            Assert.Equal(unheldSource, Assert.Single(plan.Layers).SourceTime);
        }
    }

    [Fact]
    public void SourceFrameSpan_Rejects_Held_Clips()
    {
        Clip clip = Hold(ClipAt4s(), atSeconds: 2, durationSeconds: 3);
        Assert.Throws<InvalidOperationException>(
            () => FrameHoldEdits.SourceFrameSpan(clip, Timecode.FromSeconds(5), new Rational(24, 1)));
    }
}
