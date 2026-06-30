namespace Sprocket.Core.Model;

/// <summary>
/// Pure reasoning over the project's sequence-nesting graph (PLAN.md step 23). A sequence may be nested inside
/// another as a clip, so the sequences form a directed graph (an edge container → child for each nested-sequence
/// clip); it must stay <b>acyclic</b> — a sequence can't contain itself, directly or transitively — or rendering
/// would recurse forever. The render graph guards against cycles at plan time (it tracks the sequences on the
/// recursion path); this helper lets the editor refuse an illegal nest <em>before</em> making the edit.
/// </summary>
public static class SequenceGraph
{
    /// <summary>The maximum nesting depth the render graph will descend before stopping (a guard against
    /// pathologically deep — though acyclic — chains). Sequences below this depth contribute nothing.</summary>
    public const int MaxNestingDepth = 16;

    /// <summary>
    /// Whether placing the sequence <paramref name="candidateChild"/> inside <paramref name="container"/> would
    /// create a cycle — i.e. it is the container itself, or <paramref name="container"/> is already reachable from
    /// <paramref name="candidateChild"/> through existing nested-sequence clips. The editor uses this to reject an
    /// illegal nest (you can't nest a sequence into one of its own descendants, nor into itself).
    /// </summary>
    public static bool WouldCreateCycle(Project project, SequenceId container, SequenceId candidateChild)
    {
        ArgumentNullException.ThrowIfNull(project);
        return container == candidateChild || IsReachable(project, from: candidateChild, target: container);
    }

    /// <summary>Whether <paramref name="target"/> is reachable from <paramref name="from"/> by following
    /// nested-sequence clip references (transitive containment), <paramref name="from"/> excluded.</summary>
    public static bool IsReachable(Project project, SequenceId from, SequenceId target)
    {
        ArgumentNullException.ThrowIfNull(project);
        var visited = new HashSet<SequenceId>();
        var stack = new Stack<SequenceId>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            SequenceId current = stack.Pop();
            if (!visited.Add(current))
                continue;
            if (project.GetSequence(current) is not { } seq)
                continue;
            foreach (SequenceId child in NestedSequenceIds(seq))
            {
                if (child == target)
                    return true;
                stack.Push(child);
            }
        }
        return false;
    }

    /// <summary>The distinct sequences directly nested inside <paramref name="sequence"/> (one per nested-sequence
    /// clip, deduped).</summary>
    private static IEnumerable<SequenceId> NestedSequenceIds(Sequence sequence)
    {
        var seen = new HashSet<SequenceId>();
        foreach (Track track in sequence.Timeline.Tracks)
            foreach (Clip clip in track.Clips)
                if (clip is { Kind: ClipKind.Sequence, SourceSequenceId: { } id } && seen.Add(id))
                    yield return id;
    }
}
