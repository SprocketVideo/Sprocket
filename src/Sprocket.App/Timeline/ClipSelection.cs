using System;
using System.Collections.Generic;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// The timeline's clip selection (PLAN.md step 54): an ordered set with a <see cref="Primary"/> clip. The
/// primary preserves the pre-54 single-clip surface — the Inspector, Source monitor, keyframe navigation and
/// every dialog-backed operation act on it — while batch operations act on the whole set. Kept free of
/// Avalonia types so the membership/primary-tracking rules (Ctrl-click toggle, Shift-click extend, marquee
/// replace/add, undo pruning) are unit-testable headlessly; <see cref="Timeline.TimelineControl"/> owns one
/// instance and maps pointer gestures onto these methods. Every mutator returns whether anything changed so
/// the control only raises events / repaints on a real change.
/// </summary>
public sealed class ClipSelection
{
    // Insertion-ordered membership: when the primary is toggled out, the most recently added member takes over.
    private readonly List<Clip> _clips = [];

    /// <summary>The primary clip (the single-clip surface's <c>SelectedClip</c>), or null when empty.</summary>
    public Clip? Primary { get; private set; }

    /// <summary>The selected clips in insertion order. The primary is always a member.</summary>
    public IReadOnlyList<Clip> Clips => _clips;

    /// <summary>The number of selected clips.</summary>
    public int Count => _clips.Count;

    /// <summary>Whether <paramref name="clip"/> is a member of the selection.</summary>
    public bool Contains(Clip clip) => IndexOf(clip) >= 0;

    private int IndexOf(Clip clip)
    {
        for (int i = 0; i < _clips.Count; i++)
            if (ReferenceEquals(_clips[i], clip))
                return i;
        return -1;
    }

    /// <summary>Plain click: the selection becomes just <paramref name="clip"/> (null clears it).</summary>
    public bool Replace(Clip? clip)
    {
        if (clip is null)
            return Clear();
        if (_clips.Count == 1 && ReferenceEquals(_clips[0], clip))
            return false;
        _clips.Clear();
        _clips.Add(clip);
        Primary = clip;
        return true;
    }

    /// <summary>
    /// Ctrl-click: toggles <paramref name="clip"/>'s membership. Adding makes it the primary; removing the
    /// primary hands the role to the most recently added remaining member (null when the set empties).
    /// </summary>
    public bool Toggle(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int i = IndexOf(clip);
        if (i < 0)
        {
            _clips.Add(clip);
            Primary = clip;
        }
        else
        {
            _clips.RemoveAt(i);
            if (ReferenceEquals(Primary, clip))
                Primary = _clips.Count > 0 ? _clips[^1] : null;
        }
        return true;
    }

    /// <summary>Shift-click: adds <paramref name="clip"/> to the selection (kept if already a member) and
    /// makes it the primary — extend never removes.</summary>
    public bool Extend(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (IndexOf(clip) < 0)
        {
            _clips.Add(clip);
            Primary = clip;
            return true;
        }
        if (ReferenceEquals(Primary, clip))
            return false;
        Primary = clip;
        return true;
    }

    /// <summary>Makes an existing member the primary (a plain press on a multi-selected clip keeps the set but
    /// re-anchors the primary). No-op when <paramref name="clip"/> is not a member or already primary.</summary>
    public bool SetPrimary(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (IndexOf(clip) < 0 || ReferenceEquals(Primary, clip))
            return false;
        Primary = clip;
        return true;
    }

    /// <summary>
    /// Replaces the whole selection with <paramref name="clips"/> (marquee / Select All). Duplicates are
    /// ignored; the first clip becomes the primary unless the current primary is among the new members, in
    /// which case it keeps the role (a live marquee that still covers the primary shouldn't re-anchor it).
    /// </summary>
    public bool ReplaceAll(IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        Clip? oldPrimary = Primary;
        var next = new List<Clip>();
        // Hash-set dedupe: this runs on every pointer-move of a live marquee, so it must stay O(n).
        var seen = new HashSet<Clip>(ReferenceEqualityComparer.Instance);
        foreach (Clip c in clips)
            if (seen.Add(c))
                next.Add(c);

        if (next.Count == _clips.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count; i++)
                if (!ReferenceEquals(next[i], _clips[i]))
                {
                    same = false;
                    break;
                }
            if (same)
                return false;
        }

        _clips.Clear();
        _clips.AddRange(next);
        Primary = oldPrimary is not null && Contains(oldPrimary)
            ? oldPrimary
            : (_clips.Count > 0 ? _clips[0] : null);
        return true;
    }

    /// <summary>Clears the selection.</summary>
    public bool Clear()
    {
        if (_clips.Count == 0)
            return false;
        _clips.Clear();
        Primary = null;
        return true;
    }

    /// <summary>
    /// Drops members that no longer satisfy <paramref name="alive"/> — undo/redo may have removed clips from
    /// the timeline (the pre-54 stale-selection rule, applied per member). The primary hands off like
    /// <see cref="Toggle"/> when pruned.
    /// </summary>
    public bool Prune(Func<Clip, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        int removed = _clips.RemoveAll(c => !alive(c));
        if (removed == 0)
            return false;
        if (Primary is not null && !Contains(Primary))
            Primary = _clips.Count > 0 ? _clips[^1] : null;
        return true;
    }
}
