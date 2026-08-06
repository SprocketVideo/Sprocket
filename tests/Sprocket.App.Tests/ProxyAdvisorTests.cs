using Sprocket.App.Proxy;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests for the playback drop monitor's decision logic (PLAN.md step 18): when do sustained dropped frames on a
/// software-decoded original add up to a proxy recommendation? The advisor is pure and clock-free — samples carry
/// their own timestamps — so the streak rules (thresholds, sustain window, resets, once-per-source) pin exactly.
/// </summary>
public class ProxyAdvisorTests
{
    // The app samples at ~1 Hz; these tests step in the same 1000 ms cadence.
    private const long Tick = 1000;

    private static readonly MediaRefId Media = MediaRefId.New();
    private static readonly MediaRefId OtherMedia = MediaRefId.New();

    private static ProxyAdvisorSample Sample(
        long timestampMs,
        long drops,
        MediaRefId? id = null,
        bool playing = true,
        bool software = true,
        bool difficult = true,
        bool eligible = true) =>
        new(id ?? Media, timestampMs, drops, playing, software, difficult, eligible);

    /// <summary>Feeds <paramref name="count"/> one-second samples accruing <paramref name="dropsPerTick"/> each,
    /// returning the first recommendation (or null).</summary>
    private static MediaRefId? Feed(ProxyAdvisor advisor, int count, long dropsPerTick, bool difficult = true)
    {
        for (int i = 0; i < count; i++)
        {
            if (advisor.Observe(Sample(i * Tick, i * dropsPerTick, difficult: difficult)) is { } id)
                return id;
        }
        return null;
    }

    [Fact]
    public void A_difficult_software_decoded_source_with_sustained_drops_is_recommended()
    {
        var advisor = new ProxyAdvisor();
        // 2 drops/s ≥ the difficult threshold (1/s); after the baseline sample the rate must hold for the 3 s
        // sustain window, so the recommendation lands on the 4th sample (t = 3 s).
        Assert.Equal(Media, Feed(advisor, count: 5, dropsPerTick: 2));
    }

    [Fact]
    public void An_ordinary_format_needs_the_much_higher_drop_rate()
    {
        // 2 drops/s would fire for HEVC but not for plain 1080p H.264 (threshold 5/s) — software decode
        // strengthens the case, the format decides how much evidence is enough.
        Assert.Null(Feed(new ProxyAdvisor(), count: 30, dropsPerTick: 2, difficult: false));
        Assert.Equal(Media, Feed(new ProxyAdvisor(), count: 5, dropsPerTick: 6, difficult: false));
    }

    [Fact]
    public void A_hardware_decoded_source_never_fires()
    {
        // Hardware-decoded drops are render-bound, not decode-bound — a proxy would not help.
        var advisor = new ProxyAdvisor();
        for (int i = 0; i < 30; i++)
            Assert.Null(advisor.Observe(Sample(i * Tick, i * 10, software: false)));
    }

    [Fact]
    public void A_source_that_stops_dropping_never_accrues_a_recommendation()
    {
        var advisor = new ProxyAdvisor();
        // Two seconds of heavy drops, then clean playback: the sustain window is never completed.
        Assert.Null(advisor.Observe(Sample(0, 0)));
        Assert.Null(advisor.Observe(Sample(1 * Tick, 5)));
        Assert.Null(advisor.Observe(Sample(2 * Tick, 10)));
        for (int i = 3; i < 30; i++)
            Assert.Null(advisor.Observe(Sample(i * Tick, 10)));
    }

    [Fact]
    public void Switching_sources_resets_the_streak()
    {
        var advisor = new ProxyAdvisor();
        Assert.Null(advisor.Observe(Sample(0, 0)));
        Assert.Null(advisor.Observe(Sample(1 * Tick, 3)));
        Assert.Null(advisor.Observe(Sample(2 * Tick, 6)));
        // The playhead crosses onto another clip: its drops are its own story.
        Assert.Null(advisor.Observe(Sample(3 * Tick, 9, id: OtherMedia)));
        Assert.Null(advisor.Observe(Sample(4 * Tick, 12, id: OtherMedia)));
        Assert.Null(advisor.Observe(Sample(5 * Tick, 15, id: OtherMedia)));
        // Only 3 s into the new source's streak (baseline at t=3): one more qualifying second fires it.
        Assert.Equal(OtherMedia, advisor.Observe(Sample(6 * Tick, 18, id: OtherMedia)));
    }

    [Fact]
    public void A_gap_between_samples_resets_the_streak()
    {
        var advisor = new ProxyAdvisor();
        Assert.Null(advisor.Observe(Sample(0, 0)));
        Assert.Null(advisor.Observe(Sample(1 * Tick, 3)));
        Assert.Null(advisor.Observe(Sample(2 * Tick, 6)));
        // >2.5 s of silence = playback stopped between polls; the old streak must not bridge it.
        Assert.Null(advisor.Observe(Sample(10 * Tick, 9)));
        Assert.Null(advisor.Observe(Sample(11 * Tick, 12)));
        Assert.Null(advisor.Observe(Sample(12 * Tick, 15)));
        Assert.Equal(Media, advisor.Observe(Sample(13 * Tick, 18)));
    }

    [Fact]
    public void Reset_forgets_the_streak_but_not_past_recommendations()
    {
        var advisor = new ProxyAdvisor();
        Assert.Equal(Media, Feed(advisor, count: 5, dropsPerTick: 3));

        advisor.Reset();

        // Once per source per session: even a fresh, fully qualifying streak stays quiet.
        Assert.Null(Feed(advisor, count: 30, dropsPerTick: 3));
    }

    [Fact]
    public void An_ineligible_sample_resets_the_streak()
    {
        var advisor = new ProxyAdvisor();
        Assert.Null(advisor.Observe(Sample(0, 0)));
        Assert.Null(advisor.Observe(Sample(1 * Tick, 3)));
        Assert.Null(advisor.Observe(Sample(2 * Tick, 6)));
        // The source stopped being recommendable mid-streak (e.g. its proxy just landed and the preview is on it).
        Assert.Null(advisor.Observe(Sample(3 * Tick, 9, eligible: false)));
        // Eligible again: the streak starts over from a fresh baseline, so 3 s later is the earliest fire.
        Assert.Null(advisor.Observe(Sample(4 * Tick, 12)));
        Assert.Null(advisor.Observe(Sample(5 * Tick, 15)));
        Assert.Null(advisor.Observe(Sample(6 * Tick, 18)));
        Assert.Equal(Media, advisor.Observe(Sample(7 * Tick, 21)));
    }

    [Fact]
    public void A_paused_sample_resets_the_streak()
    {
        var advisor = new ProxyAdvisor();
        Assert.Null(advisor.Observe(Sample(0, 0)));
        Assert.Null(advisor.Observe(Sample(1 * Tick, 3)));
        Assert.Null(advisor.Observe(Sample(2 * Tick, 6)));
        Assert.Null(advisor.Observe(Sample(3 * Tick, 9, playing: false)));
        Assert.Null(advisor.Observe(Sample(4 * Tick, 9)));
        Assert.Null(advisor.Observe(Sample(5 * Tick, 12)));
        Assert.Null(advisor.Observe(Sample(6 * Tick, 15)));
        Assert.Equal(Media, advisor.Observe(Sample(7 * Tick, 18)));
    }
}
