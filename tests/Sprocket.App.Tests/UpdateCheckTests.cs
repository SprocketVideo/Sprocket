using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

// Headless coverage for the channel-aware update check's pure logic (PLAN.md step 45):
// version parsing/ordering, channel filtering, per-RID asset targeting, throttling, dismissal,
// GitHub payload parsing, and the additive settings round-trip. The network/UI side
// (UpdateCheckService, the badge, the dialog) rests on manual verification like the other shell code.

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.2.0", 0, 2, 0, false)]
    [InlineData("v0.2.0", 0, 2, 0, false)]
    [InlineData("V1.10.3", 1, 10, 3, false)]
    [InlineData("0.2.0-alpha.1", 0, 2, 0, true)]
    [InlineData("v0.2.0-beta.2", 0, 2, 0, true)]
    [InlineData("0.2.0-rc.1+abc123", 0, 2, 0, true)] // build metadata ignored
    [InlineData("0.2.0+sha.deadbeef", 0, 2, 0, false)]
    [InlineData(" 1.2.3 ", 1, 2, 3, false)]
    public void Parses_valid_tags(string tag, int major, int minor, int patch, bool prerelease)
    {
        Assert.True(UpdateVersion.TryParse(tag, out UpdateVersion v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(prerelease, v.IsPrerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]        // empty prerelease label
    [InlineData("1.2.3-alpha..1")] // empty identifier
    [InlineData("1.2.3-alpha_1")]  // illegal character
    [InlineData("-1.2.3")]
    [InlineData("latest")]
    public void Rejects_malformed_tags(string? tag) =>
        Assert.False(UpdateVersion.TryParse(tag, out _));

    [Fact]
    public void Orders_per_semver()
    {
        // The PLAN.md step-45 canonical chain: 0.2.0-alpha.1 < 0.2.0-alpha.2 < 0.2.0 < 0.2.1-alpha.1.
        string[] ascending =
        [
            "0.1.9", "0.2.0-alpha.1", "0.2.0-alpha.2", "0.2.0-alpha.10", "0.2.0-beta.1",
            "0.2.0-rc.1", "0.2.0", "0.2.1-alpha.1", "0.2.1", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0",
        ];
        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.True(UpdateVersion.TryParse(ascending[i - 1], out UpdateVersion lo));
            Assert.True(UpdateVersion.TryParse(ascending[i], out UpdateVersion hi));
            Assert.True(lo < hi, $"{ascending[i - 1]} should sort below {ascending[i]}");
            Assert.True(hi > lo);
        }
    }

    [Fact]
    public void Numeric_identifiers_sort_below_alphanumeric()
    {
        Assert.True(UpdateVersion.TryParse("1.0.0-1", out UpdateVersion numeric));
        Assert.True(UpdateVersion.TryParse("1.0.0-alpha", out UpdateVersion alpha));
        Assert.True(numeric < alpha);
    }

    [Fact]
    public void Build_metadata_does_not_affect_equality()
    {
        Assert.True(UpdateVersion.TryParse("1.2.3+aaa", out UpdateVersion a));
        Assert.True(UpdateVersion.TryParse("v1.2.3+bbb", out UpdateVersion b));
        Assert.Equal(a, b);
    }
}

public class UpdateCheckTests
{
    private static UpdateRelease Release(string tag, bool prerelease, params string[] assetNames)
    {
        Assert.True(UpdateVersion.TryParse(tag, out UpdateVersion v));
        return new UpdateRelease(v, tag, prerelease, $"https://example.test/releases/{tag}",
            assetNames.Select(n => new UpdateAsset(n, $"https://example.test/dl/{n}")).ToList());
    }

    private static UpdateVersion V(string s)
    {
        Assert.True(UpdateVersion.TryParse(s, out UpdateVersion v));
        return v;
    }

    // ── Channel filtering ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stable_build_on_match_channel_ignores_prereleases()
    {
        var releases = new[] { Release("v1.1.0-alpha.1", prerelease: true), Release("v1.0.5", prerelease: false) };
        UpdateRelease? best = UpdateCheck.PickBest(V("1.0.0"), UpdateChannelPolicy.MatchCurrentChannel, releases);
        Assert.Equal("v1.0.5", best?.TagName);
    }

    [Fact]
    public void Prerelease_build_on_match_channel_sees_newer_prereleases()
    {
        var releases = new[] { Release("v0.2.0-alpha.2", prerelease: true), Release("v0.1.0", prerelease: false) };
        UpdateRelease? best = UpdateCheck.PickBest(V("0.2.0-alpha.1"), UpdateChannelPolicy.MatchCurrentChannel, releases);
        Assert.Equal("v0.2.0-alpha.2", best?.TagName);
    }

    [Fact]
    public void StableOnly_hides_prereleases_even_from_prerelease_builds()
    {
        var releases = new[] { Release("v0.2.0-alpha.2", prerelease: true) };
        Assert.Null(UpdateCheck.PickBest(V("0.2.0-alpha.1"), UpdateChannelPolicy.StableOnly, releases));
    }

    [Fact]
    public void IncludePrereleases_shows_prereleases_to_stable_builds()
    {
        var releases = new[] { Release("v1.1.0-beta.1", prerelease: true) };
        UpdateRelease? best = UpdateCheck.PickBest(V("1.0.0"), UpdateChannelPolicy.IncludePrereleases, releases);
        Assert.Equal("v1.1.0-beta.1", best?.TagName);
    }

    [Fact]
    public void Prerelease_flag_alone_marks_a_release_prerelease()
    {
        // A stable-looking tag published with GitHub's prerelease flag set must still be filtered.
        var releases = new[] { Release("v1.1.0", prerelease: true) };
        Assert.Null(UpdateCheck.PickBest(V("1.0.0"), UpdateChannelPolicy.StableOnly, releases));
    }

    [Fact]
    public void Up_to_date_and_older_releases_yield_null()
    {
        var releases = new[] { Release("v1.0.0", prerelease: false), Release("v0.9.0", prerelease: false) };
        Assert.Null(UpdateCheck.PickBest(V("1.0.0"), UpdateChannelPolicy.MatchCurrentChannel, releases));
    }

    [Fact]
    public void Picks_the_newest_of_several_acceptable_releases()
    {
        var releases = new[]
        {
            Release("v1.0.1", prerelease: false), Release("v1.2.0", prerelease: false),
            Release("v1.1.0", prerelease: false),
        };
        UpdateRelease? best = UpdateCheck.PickBest(V("1.0.0"), UpdateChannelPolicy.StableOnly, releases);
        Assert.Equal("v1.2.0", best?.TagName);
    }

    // ── Asset targeting ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("win-x64", "Sprocket-0.2.0-win-x64.zip")]
    [InlineData("linux-arm64", "Sprocket-0.2.0-linux-arm64.zip")]
    [InlineData("osx-arm64", "Sprocket-0.2.0-osx-arm64.zip")]
    public void Selects_the_rid_matching_asset(string rid, string expected)
    {
        UpdateRelease release = Release("v0.2.0", prerelease: false,
            "Sprocket-0.2.0-win-x64.zip", "Sprocket-0.2.0-win-arm64.zip", "Sprocket-0.2.0-linux-x64.zip",
            "Sprocket-0.2.0-linux-arm64.zip", "Sprocket-0.2.0-osx-x64.zip", "Sprocket-0.2.0-osx-arm64.zip");
        Assert.Equal(expected, UpdateCheck.SelectAsset(release, rid)?.Name);
    }

    [Fact]
    public void Prerelease_suffixed_zip_names_still_match()
    {
        UpdateRelease release = Release("v0.2.0-alpha.1", prerelease: true, "Sprocket-0.2.0-alpha.1-win-x64.zip");
        Assert.Equal("Sprocket-0.2.0-alpha.1-win-x64.zip", UpdateCheck.SelectAsset(release, "win-x64")?.Name);
    }

    [Fact]
    public void Missing_platform_asset_returns_null_not_a_wrong_zip()
    {
        // "win-x64" must not loose-match inside "win-arm64" or a sources zip.
        UpdateRelease release = Release("v0.2.0", prerelease: false,
            "Sprocket-0.2.0-win-arm64.zip", "source.zip", "Sprocket-0.2.0-win-x64.tar.gz");
        Assert.Null(UpdateCheck.SelectAsset(release, "win-x64"));
    }

    [Fact]
    public void Current_rid_has_the_release_scheme_shape()
    {
        string rid = UpdateCheck.CurrentRid();
        Assert.Matches("^(win|linux|osx)-(x64|arm64)$", rid);
    }

    // ── Throttling & dismissal ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Throttle_allows_first_check_and_stale_or_garbage_timestamps()
    {
        DateTime now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.ShouldCheck(null, now));
        Assert.True(UpdateCheck.ShouldCheck("", now));
        Assert.True(UpdateCheck.ShouldCheck("not-a-date", now));
        Assert.True(UpdateCheck.ShouldCheck(now.AddDays(-2).ToString("o"), now));
        Assert.True(UpdateCheck.ShouldCheck(now.AddDays(2).ToString("o"), now)); // clock rollback
    }

    [Fact]
    public void Throttle_blocks_recent_checks()
    {
        DateTime now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateCheck.ShouldCheck(now.AddHours(-1).ToString("o"), now));
        Assert.False(UpdateCheck.ShouldCheck(
            now.Add(-UpdateCheck.MinCheckInterval).AddMinutes(1).ToString("o"), now));
        Assert.True(UpdateCheck.ShouldCheck(now.Add(-UpdateCheck.MinCheckInterval).ToString("o"), now));
    }

    [Fact]
    public void Dismissed_version_stays_quiet_but_a_newer_one_notifies_again()
    {
        var info = new UpdateInfo("v0.2.0", "https://example.test", null, null);
        Assert.True(UpdateCheck.ShouldNotify(info, ""));
        Assert.True(UpdateCheck.ShouldNotify(info, null));
        Assert.False(UpdateCheck.ShouldNotify(info, "v0.2.0"));
        Assert.True(UpdateCheck.ShouldNotify(info with { TagName = "v0.2.1" }, "v0.2.0"));
        Assert.False(UpdateCheck.ShouldNotify(null, ""));
    }

    // ── Payload parsing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_a_releases_payload_and_skips_drafts_and_bad_tags()
    {
        const string json = """
        [
          { "tag_name": "v0.2.0", "prerelease": false, "draft": false, "html_url": "https://x/r/v0.2.0",
            "assets": [ { "name": "Sprocket-0.2.0-win-x64.zip", "browser_download_url": "https://x/d/a.zip" } ] },
          { "tag_name": "v0.3.0", "prerelease": true, "draft": true, "html_url": "https://x/r/v0.3.0", "assets": [] },
          { "tag_name": "nightly", "prerelease": true, "draft": false, "assets": [] },
          { "prerelease": false, "draft": false },
          { "tag_name": "v0.1.0", "prerelease": true, "draft": false,
            "assets": [ { "name": "no-url.zip" }, 42 ] }
        ]
        """;
        IReadOnlyList<UpdateRelease> releases = UpdateCheck.ParseReleases(json);
        Assert.Equal(2, releases.Count);

        UpdateRelease first = releases[0];
        Assert.Equal("v0.2.0", first.TagName);
        Assert.False(first.Prerelease);
        Assert.Equal("https://x/r/v0.2.0", first.HtmlUrl);
        UpdateAsset asset = Assert.Single(first.Assets);
        Assert.Equal("Sprocket-0.2.0-win-x64.zip", asset.Name);
        Assert.Equal("https://x/d/a.zip", asset.DownloadUrl);

        Assert.Equal("v0.1.0", releases[1].TagName);
        Assert.Empty(releases[1].Assets); // malformed asset entries dropped, release kept
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")] // object, not the expected array
    [InlineData("[ { \"tag_name\": ")]                        // truncated
    public void Malformed_payloads_degrade_to_no_releases(string? json) =>
        Assert.Empty(UpdateCheck.ParseReleases(json));

    // ── Policy parsing & settings round-trip ────────────────────────────────────────────────────

    [Theory]
    [InlineData("StableOnly", UpdateChannelPolicy.StableOnly)]
    [InlineData("matchcurrentchannel", UpdateChannelPolicy.MatchCurrentChannel)]
    [InlineData("IncludePrereleases", UpdateChannelPolicy.IncludePrereleases)]
    [InlineData("", UpdateChannelPolicy.MatchCurrentChannel)]
    [InlineData(null, UpdateChannelPolicy.MatchCurrentChannel)]
    [InlineData("Bogus", UpdateChannelPolicy.MatchCurrentChannel)]
    [InlineData("7", UpdateChannelPolicy.MatchCurrentChannel)] // numeric junk isn't a defined value
    public void Policy_parses_leniently(string? name, UpdateChannelPolicy expected) =>
        Assert.Equal(expected, UpdateCheck.ParsePolicy(name));

    [Fact]
    public void Settings_round_trip_preserves_update_fields()
    {
        var settings = new UserSettings(
            UpdateCheckEnabled: false,
            UpdateChannelPolicy: "StableOnly",
            UpdateLastCheckedUtc: "2026-07-02T12:00:00.0000000Z",
            UpdateAvailableTag: "v0.2.0",
            UpdateAvailableUrl: "https://x/r/v0.2.0",
            UpdateAvailableAssetName: "Sprocket-0.2.0-win-x64.zip",
            UpdateAvailableAssetUrl: "https://x/d/a.zip",
            UpdateDismissedTag: "v0.1.9");
        Assert.Equal(settings, UserSettingsStore.Deserialize(UserSettingsStore.Serialize(settings)));
    }

    [Fact]
    public void Old_settings_files_gain_update_defaults()
    {
        // A pre-step-45 file has none of the update fields — they must default additively.
        UserSettings settings = UserSettingsStore.Deserialize("{\"AutosaveIntervalSeconds\": 7}");
        Assert.True(settings.UpdateCheckEnabled);
        Assert.Equal(nameof(UpdateChannelPolicy.MatchCurrentChannel), settings.UpdateChannelPolicy);
        Assert.Equal("", settings.UpdateDismissedTag);
        Assert.Equal(7, settings.AutosaveIntervalSeconds);
    }

    [Fact]
    public void Clamp_normalizes_a_hand_edited_policy()
    {
        UserSettings settings = UserSettingsStore.Deserialize("{\"UpdateChannelPolicy\": \"weird\"}");
        Assert.Equal(nameof(UpdateChannelPolicy.MatchCurrentChannel), settings.UpdateChannelPolicy);
    }
}
