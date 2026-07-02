using System.Runtime.CompilerServices;
using Sprocket.Core.Model;

namespace Sprocket.Mcp;

/// <summary>
/// Session-lifetime integer ids for the model objects that have no persistent identity — clips and tracks
/// (media / sequences / multicam already carry GUIDs). Every MCP state read stamps each clip/track with its
/// id, and edit tools resolve those ids back to instances; positional indexes would go stale the moment an
/// edit inserts or removes something. Ids are lazily assigned, stable per object instance for the process
/// lifetime, and survive undo cycles because commands re-insert the <b>same</b> instances they removed.
/// Resolution scans the <b>active</b> sequence only (the surface the tools operate on).
/// Assignment and lookup happen on the model thread (all callers run inside
/// <see cref="IEditorSession.OnModelThreadAsync{T}"/>).
/// </summary>
public static class RuntimeIds
{
    private static readonly ConditionalWeakTable<object, StrongBox<int>> Ids = new();
    private static int _next;

    /// <summary>The runtime id of <paramref name="obj"/>, assigning the next id on first sight.</summary>
    public static int IdOf(object obj)
    {
        StrongBox<int> box = Ids.GetValue(obj, static _ => new StrongBox<int>(Interlocked.Increment(ref _next)));
        return box.Value;
    }

    /// <summary>Finds the clip with runtime id <paramref name="clipId"/> in the project's active sequence,
    /// also reporting its track; <see langword="null"/> when no such clip is (any longer) on the timeline.</summary>
    public static Clip? FindClip(Project project, int clipId, out Track? track)
    {
        foreach (Track t in project.Timeline.Tracks)
        {
            foreach (Clip clip in t.Clips)
            {
                if (HasId(clip, clipId))
                {
                    track = t;
                    return clip;
                }
            }
        }
        track = null;
        return null;
    }

    /// <summary>Finds the track with runtime id <paramref name="trackId"/> in the project's active sequence.</summary>
    public static Track? FindTrack(Project project, int trackId)
    {
        foreach (Track t in project.Timeline.Tracks)
            if (HasId(t, trackId))
                return t;
        return null;
    }

    /// <summary>Finds the transition with runtime id <paramref name="transitionId"/> in the project's active
    /// sequence, also reporting its track; <see langword="null"/> when no such transition exists.</summary>
    public static Transition? FindTransition(Project project, int transitionId, out Track? track)
    {
        foreach (Track t in project.Timeline.Tracks)
        {
            foreach (Transition transition in t.Transitions)
            {
                if (HasId(transition, transitionId))
                {
                    track = t;
                    return transition;
                }
            }
        }
        track = null;
        return null;
    }

    /// <summary>Whether <paramref name="obj"/> already carries id <paramref name="id"/> — a pure lookup that
    /// never assigns, so probing can't burn ids or match objects the client has never been shown.</summary>
    private static bool HasId(object obj, int id) =>
        Ids.TryGetValue(obj, out StrongBox<int>? box) && box.Value == id;
}
