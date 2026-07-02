using System;
using System.Collections.Generic;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure logic for the on-timeline fade handles and opacity rubber-band (PLAN.md step 39): reading a clip's
/// fade-in/out lengths from its Fade effect's opacity keyframes, rebuilding the envelope for a handle drag,
/// and the rubber-band's grab/adjust operations. Kept free of Avalonia types so the envelope semantics are
/// unit-tested headlessly; the drawing + pointer interaction in <see cref="Timeline.TimelineControl"/> rest on
/// this. The fade is the existing keyframed <see cref="EffectTypeIds.Fade"/> /
/// <see cref="EffectParamNames.Opacity"/> <see cref="AnimatableValue"/> that drives both video alpha (shader)
/// and audio gain (mixer), so these edits stay in sync with the Inspector's keyframe lane — every mutation
/// goes through <see cref="Sprocket.Core.Commands.SetClipFadeCommand"/> (undoable by construction, step 10).
/// </summary>
public static class FadeOps
{
    /// <summary>The clip's first enabled Fade effect, or <see langword="null"/> when it has none.</summary>
    public static EffectInstance? FindFade(Clip clip)
    {
        foreach (EffectInstance e in clip.Effects)
            if (e.Enabled && e.EffectTypeId == EffectTypeIds.Fade)
                return e;
        return null;
    }

    /// <summary>The clip's fade opacity envelope, or <see langword="null"/> when there is no Fade effect (which
    /// renders as a constant 1.0 — the "no fade" rest state).</summary>
    public static AnimatableValue? FadeOpacity(Clip clip) =>
        FindFade(clip) is { } fade && fade.Parameters.TryGetValue(EffectParamNames.Opacity, out AnimatableValue? v)
            ? v
            : null;

    /// <summary>
    /// Reads the clip's fade-in/out lengths (timeline ticks) from its opacity envelope: a fade-in exists when
    /// the envelope's first keyframe sits at (or before) the clip's start and ramps <em>up</em> to the second;
    /// a fade-out when the last keyframe sits at (or past) the clip's end and the envelope ramps <em>down</em>
    /// into it. This recognises the canonical shape the fade handles author (and the historical bootstrap
    /// fade); an arbitrary hand-keyframed envelope that doesn't ramp at the edges reads as 0/0 — the handles
    /// then sit at the corners and dragging one rewrites the edge ramp.
    /// </summary>
    public static (long FadeInTicks, long FadeOutTicks) ReadFades(AnimatableValue? opacity, long startTicks, long endTicks)
    {
        if (opacity is null || !opacity.IsAnimated || opacity.Keyframes.Count < 2)
            return (0, 0);

        IReadOnlyList<Keyframe> kfs = opacity.Keyframes;
        long duration = Math.Max(0, endTicks - startTicks);
        long fadeIn = 0, fadeOut = 0;

        if (kfs[0].Time.Ticks <= startTicks && kfs[0].Value < kfs[1].Value)
            fadeIn = Math.Clamp(kfs[1].Time.Ticks - startTicks, 0, duration);
        if (kfs[^1].Time.Ticks >= endTicks && kfs[^1].Value < kfs[^2].Value)
            fadeOut = Math.Clamp(endTicks - kfs[^2].Time.Ticks, 0, duration);
        return (fadeIn, fadeOut);
    }

    /// <summary>Convenience overload reading from the clip's current envelope and placement.</summary>
    public static (long FadeInTicks, long FadeOutTicks) ReadFades(Clip clip) =>
        ReadFades(FadeOpacity(clip), clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks);

    /// <summary>
    /// Builds the opacity envelope for new fade-in/out lengths, preserving the envelope's <em>interior</em>
    /// keyframes (the rubber-band points strictly between the old edge ramps that still fall inside the new
    /// plateau). The ramps land exactly on the clip's edges — (start, 0) → (start + fadeIn, level) and
    /// (end − fadeOut, level) → (end, 0) — where each ramp's inner level is the current envelope's value at
    /// that boundary (1.0 when there is no envelope yet), so a handle drag doesn't flatten a lowered
    /// rubber-band. Both fades zero with no interior points collapses back to a constant. The caller passes
    /// the envelope captured at drag start so repeated drag updates are computed from one stable original.
    /// </summary>
    public static AnimatableValue BuildOpacity(
        AnimatableValue? existing, long startTicks, long endTicks, long fadeInTicks, long fadeOutTicks)
    {
        long duration = Math.Max(0, endTicks - startTicks);
        fadeInTicks = Math.Clamp(fadeInTicks, 0, duration);
        fadeOutTicks = Math.Clamp(fadeOutTicks, 0, duration - fadeInTicks);

        (long oldIn, long oldOut) = ReadFades(existing, startTicks, endTicks);
        long inBoundary = startTicks + fadeInTicks;
        long outBoundary = endTicks - fadeOutTicks;

        // The plateau level each ramp meets: evaluate the current envelope at the boundary, but never inside
        // its OLD edge ramps (a shrunk fade would otherwise sample mid-ramp and read an artificially low top).
        double levelIn = existing?.Evaluate(new Timecode(Math.Max(inBoundary, startTicks + oldIn))) ?? 1.0;
        double levelOut = existing?.Evaluate(new Timecode(Math.Min(outBoundary, endTicks - oldOut))) ?? 1.0;

        var kfs = new List<Keyframe>();
        if (fadeInTicks > 0)
        {
            kfs.Add(new Keyframe(new Timecode(startTicks), 0));
            kfs.Add(new Keyframe(new Timecode(inBoundary), levelIn));
        }
        if (existing is { IsAnimated: true })
        {
            foreach (Keyframe k in existing.Keyframes)
            {
                long t = k.Time.Ticks;
                bool interiorOfOld = t > startTicks + oldIn && t < endTicks - oldOut;
                bool insideNewPlateau = t > inBoundary && t < outBoundary;
                if (interiorOfOld && insideNewPlateau)
                    kfs.Add(k);
            }
        }
        if (fadeOutTicks > 0)
        {
            if (kfs.Count == 0 || kfs[^1].Time.Ticks < outBoundary) // fades meeting at one point share a peak
                kfs.Add(new Keyframe(new Timecode(outBoundary), levelOut));
            kfs.Add(new Keyframe(new Timecode(endTicks), 0));
        }

        return kfs.Count == 0 ? AnimatableValue.Constant(levelIn) : AnimatableValue.Animated(kfs);
    }

    /// <summary>
    /// The keyframe times a rubber-band grab at <paramref name="timeTicks"/> takes hold of: the single nearest
    /// keyframe when one sits within <paramref name="toleranceTicks"/> (point editing — e.g. right after a
    /// Ctrl+click added one), otherwise the two keyframes bounding the grabbed segment (the Premiere band-drag
    /// convention: pulling between two points moves the whole segment). Empty for a constant envelope — the
    /// caller then drags the flat level instead.
    /// </summary>
    public static IReadOnlyList<long> GrabKeyframes(AnimatableValue? opacity, long timeTicks, long toleranceTicks)
    {
        if (opacity is null || !opacity.IsAnimated)
            return [];

        long nearest = long.MinValue;
        long nearestDist = toleranceTicks;
        foreach (Keyframe k in opacity.Keyframes)
        {
            long d = Math.Abs(k.Time.Ticks - timeTicks);
            if (d <= nearestDist)
            {
                nearestDist = d;
                nearest = k.Time.Ticks;
            }
        }
        if (nearest != long.MinValue)
            return [nearest];

        long? before = null, after = null;
        foreach (Keyframe k in opacity.Keyframes)
        {
            if (k.Time.Ticks <= timeTicks)
            {
                before = k.Time.Ticks;
            }
            else
            {
                after = k.Time.Ticks;
                break;
            }
        }
        if (before is { } b && after is { } a)
            return [b, a];
        // Outside the keyframe range the envelope is clamped flat — drag the clamping end keyframe.
        return before is { } only ? [only] : after is { } first ? [first] : [];
    }

    /// <summary>
    /// The envelope with <paramref name="delta"/> added to the value of every keyframe whose time is in
    /// <paramref name="times"/> (clamped to [0, 1]) — the rubber-band vertical drag. A constant envelope (or a
    /// missing one, passed as <see langword="null"/> = 1.0) moves as a whole flat level. The caller passes the
    /// envelope and grab captured at drag start and the <em>total</em> delta each update, so the drag is exact
    /// regardless of pointer event granularity.
    /// </summary>
    public static AnimatableValue WithValueDelta(AnimatableValue? opacity, IReadOnlyCollection<long> times, double delta)
    {
        if (opacity is null || !opacity.IsAnimated)
        {
            double level = opacity?.Evaluate(Timecode.Zero) ?? 1.0;
            return AnimatableValue.Constant(Math.Clamp(level + delta, 0, 1));
        }

        var selected = new HashSet<long>(times);
        var kfs = new List<Keyframe>(opacity.Keyframes.Count);
        foreach (Keyframe k in opacity.Keyframes)
            kfs.Add(selected.Contains(k.Time.Ticks) ? k with { Value = Math.Clamp(k.Value + delta, 0, 1) } : k);
        return AnimatableValue.Animated(kfs);
    }

    /// <summary>
    /// The envelope with a keyframe added at <paramref name="timeTicks"/> carrying the current evaluated level
    /// — the rubber-band's Ctrl+click "add point" (an existing keyframe at that exact time is left as is). A
    /// constant/missing envelope becomes animated at its current flat level.
    /// </summary>
    public static AnimatableValue WithAddedPoint(AnimatableValue? opacity, long timeTicks)
    {
        var t = new Timecode(timeTicks);
        double level = opacity?.Evaluate(t) ?? 1.0;
        if (opacity is null || !opacity.IsAnimated)
            return AnimatableValue.Animated([new Keyframe(t, level)]);

        var kfs = new List<Keyframe>(opacity.Keyframes.Count + 1);
        foreach (Keyframe k in opacity.Keyframes)
        {
            if (k.Time.Ticks == timeTicks)
                return opacity;
            kfs.Add(k);
        }
        kfs.Add(new Keyframe(t, level));
        return AnimatableValue.Animated(kfs);
    }
}
