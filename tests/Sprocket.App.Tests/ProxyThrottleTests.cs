using Sprocket.App.Proxy;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the two pure decisions behind proxy generation's background manners (PLAN.md step 18):
/// how hard the encoder child is allowed to push the machine while the preview is live, and what the status bar
/// says about the build. The child process, its priority, and the event plumbing rest on manual verification
/// (the App is a UI-bound WinExe).
/// </summary>
public class ProxyThrottleTests
{
    [Theory]
    [InlineData(1, 1)]   // single core: still needs one thread
    [InlineData(2, 1)]
    [InlineData(4, 2)]   // the modest-laptop case the throttle exists for: half the cores stay free
    [InlineData(8, 4)]
    [InlineData(16, 8)]  // capped — more x264 threads than this buys nothing on a proxy encode
    [InlineData(64, 8)]
    public void EncodeThreadCount_is_half_the_cores_floored_at_one_and_capped(int cores, int expected) =>
        Assert.Equal(expected, ProxyTranscoder.EncodeThreadCount(cores));

    [Fact]
    public void EncodeThreadCount_never_returns_a_nonsense_count_for_a_bogus_core_count() =>
        Assert.Equal(1, ProxyTranscoder.EncodeThreadCount(0));

    // ── -progress parsing (the per-file progress readout in the Proxy window) ──────────────────────

    /// <summary>Ten seconds at the global 240000-tick base — a plausible source duration to measure against.</summary>
    private const long TenSecondsTicks = 10 * Timecode.TicksPerSecond;

    [Theory]
    [InlineData("out_time_us=0", 0.0)]
    [InlineData("out_time_us=5000000", 0.5)]     // 5s of a 10s source
    [InlineData("out_time_us=10000000", 1.0)]
    [InlineData("out_time_us=99000000", 1.0)]    // clamped: a source can report slightly past its probed duration
    public void ProgressFraction_reads_the_output_position_against_the_source_duration(string line, double expected) =>
        Assert.Equal(expected, ProxyTranscoder.ProgressFraction(line, TenSecondsTicks)!.Value, 3);

    [Theory]
    [InlineData("frame=42")]              // some other key from the same -progress block
    [InlineData("out_time_us=N/A")]       // ffmpeg's placeholder before the first frame is written
    [InlineData("out_time_ms=5000")]      // near-miss key
    [InlineData("")]
    [InlineData(null)]
    public void ProgressFraction_ignores_lines_that_carry_no_position(string? line) =>
        Assert.Null(ProxyTranscoder.ProgressFraction(line, TenSecondsTicks));

    [Fact]
    public void ProgressFraction_reports_nothing_when_the_source_duration_is_unknown() =>
        Assert.Null(ProxyTranscoder.ProgressFraction("out_time_us=5000000", durationTicks: 0));

    // ── Status-bar wording ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_is_null_when_no_proxy_was_ever_wanted() =>
        Assert.Null(ProxyService.FormatSummary(ready: 0, pending: 0, failed: 0));

    [Fact]
    public void Summary_reports_the_backlog_while_work_remains() =>
        Assert.Equal("building proxies… 2 ready, 3 pending", ProxyService.FormatSummary(2, 3, 0));

    [Fact]
    public void Summary_settles_when_the_queue_drains() =>
        Assert.Equal("proxies ready (4)", ProxyService.FormatSummary(4, 0, 0));

    [Fact]
    public void Summary_surfaces_failures_alongside_successes() =>
        Assert.Equal("proxies ready (4), 1 failed", ProxyService.FormatSummary(4, 0, 1));

    [Fact]
    public void Summary_explains_a_run_where_everything_failed()
    {
        // The regression this guards: a failed build reported nothing at all, so a run whose only source failed
        // left "building proxies… 0 ready, 1 pending" on screen forever with no explanation.
        Assert.Equal("proxy generation failed (2) — previewing originals", ProxyService.FormatSummary(0, 0, 2));
    }
}
