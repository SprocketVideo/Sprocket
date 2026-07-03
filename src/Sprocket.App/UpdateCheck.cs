using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Sprocket.App;

/// <summary>Which published releases an update check may notify about (PLAN.md step 45).</summary>
public enum UpdateChannelPolicy
{
    /// <summary>Only stable releases — prereleases are invisible even to a prerelease build.</summary>
    StableOnly,

    /// <summary>Stable builds see only stable releases; alpha/beta/rc builds also see newer prereleases.
    /// The default: it preserves the current all-prerelease release flow without opting stable users in.</summary>
    MatchCurrentChannel,

    /// <summary>Every published release, prerelease or not — an explicit opt-in.</summary>
    IncludePrereleases,
}

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl);

/// <summary>A published (non-draft) GitHub release with a parseable version tag.</summary>
public sealed record UpdateRelease(
    UpdateVersion Version, string TagName, bool Prerelease, string HtmlUrl, IReadOnlyList<UpdateAsset> Assets);

/// <summary>The user-facing result of a successful check: what is available and where to get it.
/// <see cref="AssetUrl"/> is <see langword="null"/> when no asset matches this platform — the UI then
/// deep-links to the release page instead of hiding the update.</summary>
public sealed record UpdateInfo(string TagName, string HtmlUrl, string? AssetName, string? AssetUrl)
{
    /// <summary>The tag without its leading <c>v</c>, for display ("0.2.0-alpha.1").</summary>
    public string DisplayVersion => TagName.TrimStart('v', 'V');
}

/// <summary>
/// The pure, headlessly-tested decision logic behind the update check (PLAN.md step 45): GitHub
/// releases-list parsing, channel filtering, per-RID asset targeting, launch throttling, and
/// dismissal. The network/UI side lives in <see cref="UpdateCheckService"/>. The releases *list* is
/// used deliberately — GitHub's <c>releases/latest</c> means "newest non-prerelease", which reports
/// "up to date" forever to an alpha-only project.
/// </summary>
public static class UpdateCheck
{
    /// <summary>Minimum spacing between automatic startup checks; Help ▸ Check for Updates bypasses it.</summary>
    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours(20);

    /// <summary>Parses <paramref name="policyName"/> leniently; anything unrecognized falls back to
    /// the <see cref="UpdateChannelPolicy.MatchCurrentChannel"/> default (a hand-edited settings file
    /// must not disable or widen the check surprisingly).</summary>
    public static UpdateChannelPolicy ParsePolicy(string? policyName) =>
        Enum.TryParse(policyName, ignoreCase: true, out UpdateChannelPolicy policy) &&
        Enum.IsDefined(policy)
            ? policy
            : UpdateChannelPolicy.MatchCurrentChannel;

    /// <summary>
    /// Parses a GitHub <c>/releases</c> JSON array into releases the picker can reason about. Drafts,
    /// entries with missing/malformed tags, and malformed asset entries are skipped; a payload that is
    /// not a JSON array (or not JSON at all) yields an empty list — a bad API response degrades to
    /// "no update found", never an exception.
    /// </summary>
    public static IReadOnlyList<UpdateRelease> ParseReleases(string? json)
    {
        var releases = new List<UpdateRelease>();
        if (string.IsNullOrWhiteSpace(json))
            return releases;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return releases;
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                if (e.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                    continue;
                if (!e.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String ||
                    !UpdateVersion.TryParse(tag.GetString(), out UpdateVersion version))
                    continue;

                bool prerelease = e.TryGetProperty("prerelease", out JsonElement p) && p.ValueKind == JsonValueKind.True;
                string htmlUrl = e.TryGetProperty("html_url", out JsonElement u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()! : "";

                var assets = new List<UpdateAsset>();
                if (e.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in list.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.Object &&
                            a.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String &&
                            a.TryGetProperty("browser_download_url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
                            assets.Add(new UpdateAsset(name.GetString()!, url.GetString()!));
                    }
                }
                releases.Add(new UpdateRelease(version, tag.GetString()!, prerelease, htmlUrl, assets));
            }
        }
        catch (JsonException)
        {
            releases.Clear();
        }
        return releases;
    }

    /// <summary>
    /// Picks the newest release that is both acceptable under <paramref name="policy"/> and strictly
    /// newer than <paramref name="current"/>, or <see langword="null"/> when up to date. A release
    /// counts as prerelease if either GitHub's flag or its version label says so.
    /// </summary>
    public static UpdateRelease? PickBest(
        UpdateVersion current, UpdateChannelPolicy policy, IEnumerable<UpdateRelease> releases)
    {
        bool prereleasesAllowed = policy switch
        {
            UpdateChannelPolicy.StableOnly => false,
            UpdateChannelPolicy.IncludePrereleases => true,
            _ => current.IsPrerelease, // MatchCurrentChannel
        };

        UpdateRelease? best = null;
        foreach (UpdateRelease r in releases)
        {
            if (!prereleasesAllowed && (r.Prerelease || r.Version.IsPrerelease))
                continue;
            if (r.Version <= current)
                continue;
            if (best is null || r.Version > best.Version)
                best = r;
        }
        return best;
    }

    /// <summary>Finds the release asset built for <paramref name="rid"/> under the
    /// <c>scripts/release.ps1</c> naming scheme (<c>Sprocket-&lt;version&gt;-&lt;rid&gt;.zip</c>), or
    /// <see langword="null"/> — the caller then offers the release page instead.</summary>
    public static UpdateAsset? SelectAsset(UpdateRelease release, string rid) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>This build's runtime identifier in the release-asset scheme (e.g. <c>win-x64</c>).</summary>
    public static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }

    /// <summary>
    /// Whether an automatic check is due: the stored round-trip timestamp is absent/unreadable, in the
    /// future (clock rollback), or at least <see cref="MinCheckInterval"/> old.
    /// </summary>
    public static bool ShouldCheck(string? lastCheckedUtc, DateTime nowUtc)
    {
        if (!DateTime.TryParse(lastCheckedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime last))
            return true;
        return last > nowUtc || nowUtc - last >= MinCheckInterval;
    }

    /// <summary>Whether the non-modal badge should show for <paramref name="info"/>: yes unless the
    /// user already dismissed exactly this version (the no-nagging rule).</summary>
    public static bool ShouldNotify(UpdateInfo? info, string? dismissedTag) =>
        info is not null && !string.Equals(info.TagName, dismissedTag, StringComparison.Ordinal);
}
