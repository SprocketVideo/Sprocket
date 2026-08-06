using System;
using System.Collections.Generic;
using Sprocket.Core.Model;

namespace Sprocket.App.Proxy;

/// <summary>One telemetry-tick observation of the playing preview, as fed to <see cref="ProxyAdvisor.Observe"/>.</summary>
/// <param name="MediaId">The source the preview is currently decoding (the engine's top-most active media clip).</param>
/// <param name="TimestampMs">A monotonic clock reading (e.g. <see cref="Environment.TickCount64"/>).</param>
/// <param name="CumulativeDrops">The engine's monotonic dropped-frame counter
/// (<c>PlaybackStatistics.FramesDropped</c>) — the advisor derives a drops/second rate from deltas.</param>
/// <param name="IsPlaying">Whether the transport is playing. Scrub/seek samples never contribute.</param>
/// <param name="IsSoftwareDecoded">Whether the active decoder is running without hardware acceleration.
/// Hardware-decoded drops are render-bound, not decode-bound — a proxy would not help, so they never fire.</param>
/// <param name="IsDifficultFormat">Whether the source's probed format is expensive to decode
/// (<c>ProxyPolicy.IsDemandingFormat</c>). Difficult formats recommend at a lower sustained drop rate —
/// software decode <em>strengthens</em> the case rather than triggering by itself.</param>
/// <param name="IsEligible">Whether the source could still use a recommendation (proxy state
/// <c>NotNeeded</c>). A source already recommended, queued, building, or playing off its proxy is not.</param>
public readonly record struct ProxyAdvisorSample(
    MediaRefId MediaId,
    long TimestampMs,
    long CumulativeDrops,
    bool IsPlaying,
    bool IsSoftwareDecoded,
    bool IsDifficultFormat,
    bool IsEligible);

/// <summary>
/// The playback drop monitor's decision logic (PLAN.md step 18): watches the telemetry samples the app already
/// polls (~1 s cadence while playing) and decides when sustained dropped frames on a software-decoded original
/// justify recommending a proxy. Pure and clock-free — the caller supplies timestamps — so the streak rules are
/// unit-testable without an engine.
/// </summary>
/// <remarks>
/// <para>A recommendation fires only after an unbroken streak of qualifying samples: same source, playing,
/// software-decoded, still eligible, and dropping above the rate threshold, sustained for at least
/// <paramref name="sustainSeconds"/>. Any disqualifying or stale sample (a gap over <see cref="MaxSampleGapMs"/> —
/// playback stopped between polls) resets the streak, so a warm-up hiccup or a single bad tick never fires.</para>
/// <para>Two thresholds implement "software decode strengthens, but does not trigger": a difficult format
/// (HEVC/AV1/10-bit/4:2:2 — <c>ProxyPolicy.IsDemandingFormat</c>) recommends at
/// <paramref name="difficultDropsPerSec"/>, while an ordinary format (1080p 8-bit H.264, which usually plays fine
/// in software) needs the much higher <paramref name="plainDropsPerSec"/> before the advisor concludes the
/// machine genuinely cannot keep up.</para>
/// <para>Each source is recommended <b>at most once per session</b> — the recommendation is a nudge, not a nag;
/// declining it (not clicking Generate) must not produce a re-recommendation on the next play. This is a
/// deliberate departure from Resolve/Premiere, which surface no telemetry-driven proxy advice at all.</para>
/// </remarks>
/// <param name="difficultDropsPerSec">Sustained drops/second that fires for a difficult format.</param>
/// <param name="plainDropsPerSec">Sustained drops/second that fires for an ordinary format.</param>
/// <param name="sustainSeconds">How long the rate must hold before the recommendation fires.</param>
public sealed class ProxyAdvisor(
    double difficultDropsPerSec = 1.0,
    double plainDropsPerSec = 5.0,
    double sustainSeconds = 3.0)
{
    /// <summary>The largest gap between consecutive samples that still counts as one continuous observation —
    /// generous against the app's ~1 s telemetry cadence, tight enough that a stop/start between polls resets.</summary>
    internal const long MaxSampleGapMs = 2500;

    private readonly HashSet<MediaRefId> _alreadyRecommended = [];

    private MediaRefId? _streakId;
    private long _streakStartMs;
    private long _lastSampleMs;
    private long _lastDrops;

    /// <summary>
    /// Feeds one observation. Returns the source to recommend a proxy for when this sample completes a qualifying
    /// streak, else <see langword="null"/>. At most one non-null return per source per advisor lifetime.
    /// </summary>
    public MediaRefId? Observe(ProxyAdvisorSample sample)
    {
        bool qualifies = sample.IsPlaying
            && sample.IsSoftwareDecoded
            && sample.IsEligible
            && !_alreadyRecommended.Contains(sample.MediaId);

        if (!qualifies || (_streakId is not null && sample.MediaId != _streakId)
            || (_streakId is not null && sample.TimestampMs - _lastSampleMs > MaxSampleGapMs))
        {
            ResetStreak();
            if (!qualifies)
                return null;
        }

        if (_streakId is null)
        {
            // First sample of a potential streak: baseline only — a rate needs two points.
            _streakId = sample.MediaId;
            _streakStartMs = sample.TimestampMs;
            _lastSampleMs = sample.TimestampMs;
            _lastDrops = sample.CumulativeDrops;
            return null;
        }

        long elapsedMs = sample.TimestampMs - _lastSampleMs;
        long dropped = sample.CumulativeDrops - _lastDrops;
        _lastSampleMs = sample.TimestampMs;
        _lastDrops = sample.CumulativeDrops;

        double threshold = sample.IsDifficultFormat ? difficultDropsPerSec : plainDropsPerSec;
        double dropsPerSec = elapsedMs > 0 ? dropped * 1000.0 / elapsedMs : 0;
        if (dropsPerSec < threshold)
        {
            // The rate fell below the bar: whatever was accruing was not *sustained*. Start over from here.
            _streakStartMs = sample.TimestampMs;
            return null;
        }

        if (sample.TimestampMs - _streakStartMs < (long)(sustainSeconds * 1000))
            return null;

        _alreadyRecommended.Add(sample.MediaId);
        ResetStreak();
        return sample.MediaId;
    }

    /// <summary>Forgets the in-progress streak (playback stopped, the project changed). Sources already
    /// recommended stay recommended — <see cref="Reset"/> never re-arms a nudge the user declined.</summary>
    public void Reset() => ResetStreak();

    private void ResetStreak()
    {
        _streakId = null;
        _streakStartMs = 0;
        _lastSampleMs = 0;
        _lastDrops = 0;
    }
}
