using Sprocket.Core.Commands;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Builds the "nest selected clips into a new sequence" edit (PLAN.md step 23, the Premiere "nest" / Final Cut
/// "compound clip" gesture): the selected clips move into a brand-new child <see cref="Sequence"/>, and the
/// parent gets a single nested-sequence clip in their place (a linked video + audio pair when the selection spans
/// both). The whole thing is one undoable <see cref="CompositeCommand"/>. Pure model reasoning, so it is tested
/// headlessly even though the "what is selected" comes from the (UI-bound) timeline.
/// </summary>
public static class SequenceNesting
{
    /// <summary>The built command, the new child sequence, and the parent's primary nested clip (to select).</summary>
    public sealed record NestResult(CompositeCommand Command, Sequence Child, Clip PrimaryClip);

    /// <summary>
    /// Builds the nest command for <paramref name="clips"/> (a selection drawn from <paramref name="parent"/>),
    /// or <see langword="null"/> when none of them belong to the parent sequence. The new child sequence keeps
    /// the parent's render format and gets one track per contributing parent track (so z-order and track
    /// separation survive); the selected clips move there rebased so the earliest sits at the child's origin. The
    /// parent's replacement nested clip(s) span the selection's full extent and inherit the original tracks'
    /// compositing (opacity/blend, gain), so the picture and sound are unchanged.
    /// </summary>
    public static NestResult? CreateNest(Project project, Sequence parent, IReadOnlyList<Clip> clips, string childName)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(clips);

        // Group the selection by the parent track each clip lives on (in z-order); drop clips not in the parent.
        var byTrack = new List<(Track ParentTrack, List<Clip> Selected)>();
        foreach (Track track in parent.Timeline.Tracks)
        {
            var selected = track.Clips.Where(clips.Contains).ToList();
            if (selected.Count > 0)
                byTrack.Add((track, selected));
        }
        if (byTrack.Count == 0)
            return null;

        long minTicks = long.MaxValue, maxTicks = long.MinValue;
        foreach ((_, List<Clip> selected) in byTrack)
            foreach (Clip c in selected)
            {
                minTicks = Math.Min(minTicks, c.TimelineStart.Ticks);
                maxTicks = Math.Max(maxTicks, c.TimelineEnd.Ticks);
            }
        var minStart = new Timecode(minTicks);
        var nestStart = minStart;
        var nestDuration = new Timecode(maxTicks - minTicks);

        // The child sequence: same format, one (neutral) track per contributing parent track, empty for now.
        var childTimeline = new Timeline(parent.Timeline.FrameRate, parent.Timeline.Resolution, parent.Timeline.SampleRate);
        var child = new Sequence(SequenceId.New(), childName, childTimeline);

        var moves = new List<IEditCommand>();
        VideoTrack? firstParentVideo = null;
        AudioTrack? firstParentAudio = null;
        foreach ((Track parentTrack, List<Clip> selected) in byTrack)
        {
            Track childTrack = parentTrack is VideoTrack
                ? new VideoTrack { Name = parentTrack.Name }
                : new AudioTrack { Name = parentTrack.Name };
            childTimeline.Tracks.Add(childTrack);

            if (parentTrack is VideoTrack pv)
                firstParentVideo ??= pv;
            else if (parentTrack is AudioTrack pa)
                firstParentAudio ??= pa;

            foreach (Clip c in selected)
                moves.Add(new MoveClipToTrackCommand(parentTrack, childTrack, c, c.TimelineStart - minStart, "Nest clip"));
        }

        // The replacement nested clip(s) in the parent — a linked video + audio pair when the selection has both.
        Guid? link = firstParentVideo is not null && firstParentAudio is not null ? Guid.NewGuid() : null;
        var inserts = new List<IEditCommand>();
        Clip? primary = null;
        if (firstParentVideo is not null)
        {
            var v = Clip.CreateSequenceClip(child.Id, nestDuration, nestStart);
            v.LinkGroupId = link;
            inserts.Add(new AddClipCommand(firstParentVideo, v));
            primary = v;
        }
        if (firstParentAudio is not null)
        {
            var a = Clip.CreateSequenceClip(child.Id, nestDuration, nestStart);
            a.LinkGroupId = link;
            inserts.Add(new AddClipCommand(firstParentAudio, a));
            primary ??= a;
        }

        var commands = new List<IEditCommand> { new AddSequenceCommand(project, child) };
        commands.AddRange(moves);
        commands.AddRange(inserts);
        return new NestResult(new CompositeCommand("Nest into sequence", commands), child, primary!);
    }
}
