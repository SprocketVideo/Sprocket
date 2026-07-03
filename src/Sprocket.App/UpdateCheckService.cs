using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Sprocket.App;

/// <summary>
/// The app-scoped, channel-aware update check (PLAN.md step 45) — notification + download deep-link
/// only; it never downloads, replaces binaries, or runs installers. Modeled on
/// <see cref="McpServerService"/>: constructed by <see cref="App"/>, it outlives window swaps and the
/// shell mirrors its state into a status-bar badge. Fired asynchronously after the main window is up
/// (never blocking startup), it queries the public GitHub releases *list* anonymously (no
/// credentials, no MCP involvement), filters by the user's channel policy, and caches the result in
/// the user settings so throttled launches can still surface it offline. All members are meant to be
/// used from the UI thread (calls are awaited from it, so continuations land back there).
/// </summary>
internal sealed class UpdateCheckService : IDisposable
{
    /// <summary>What a <see cref="CheckAsync"/> call concluded, for Help ▸ Check for Updates feedback.</summary>
    public enum Outcome
    {
        /// <summary>Automatic checks are switched off in Preferences (auto path only).</summary>
        Disabled,

        /// <summary>Skipped — a recent check exists; any cached result was restored.</summary>
        Throttled,

        /// <summary>A newer acceptable release exists (see <see cref="Available"/>).</summary>
        UpdateAvailable,

        /// <summary>The releases list held nothing newer for this channel.</summary>
        UpToDate,

        /// <summary>The check could not run (network/API failure — see <see cref="LastError"/>).</summary>
        Failed,
    }

    private const string ReleasesUrl = "https://api.github.com/repos/drittich/sprocket/releases?per_page=30";

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>The newest acceptable release found (or restored from cache); <see langword="null"/>
    /// when up to date, disabled, or never checked.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>The failure message when the last check <see cref="Outcome.Failed"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised (on the UI thread) after <see cref="Available"/> may have changed.</summary>
    public event Action? StateChanged;

    public UpdateCheckService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sprocket", Program.AppVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Runs one check. The automatic path (<paramref name="force"/> false) honours the Preferences
    /// enable switch and the cross-launch throttle; Help ▸ Check for Updates passes <see langword="true"/>
    /// to bypass both. Network failures leave any previously shown result in place.
    /// </summary>
    public async Task<Outcome> CheckAsync(UserSettings settings, bool force)
    {
        if (!force && !settings.UpdateCheckEnabled)
        {
            SetAvailable(null);
            return Outcome.Disabled;
        }

        if (!UpdateVersion.TryParse(Program.AppVersion, out UpdateVersion current))
        {
            // A dev/unstamped build ("unknown") has no orderable version — nothing sane to compare against.
            LastError = $"The running version \"{Program.AppVersion}\" is not comparable.";
            return Outcome.Failed;
        }

        if (!force && !UpdateCheck.ShouldCheck(settings.UpdateLastCheckedUtc, DateTime.UtcNow))
        {
            SetAvailable(RestoreCached(settings, current));
            return Outcome.Throttled;
        }

        string json;
        try
        {
            json = await _http.GetStringAsync(ReleasesUrl, _disposed.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            LastError = "Could not reach github.com to check for updates.";
            return Outcome.Failed; // keep whatever was already shown; try again next window
        }

        UpdateChannelPolicy policy = UpdateCheck.ParsePolicy(settings.UpdateChannelPolicy);
        UpdateRelease? best = UpdateCheck.PickBest(current, policy, UpdateCheck.ParseReleases(json));
        UpdateInfo? info = null;
        if (best is not null)
        {
            UpdateAsset? asset = UpdateCheck.SelectAsset(best, UpdateCheck.CurrentRid());
            info = new UpdateInfo(best.TagName, best.HtmlUrl, asset?.Name, asset?.DownloadUrl);
        }

        LastError = null;
        PersistResult(info);
        SetAvailable(info);
        return info is null ? Outcome.UpToDate : Outcome.UpdateAvailable;
    }

    /// <summary>Rebuilds a result from the settings-cached fields of an earlier successful check, if it
    /// still names something newer than the running build (a throttled launch stays informed offline).</summary>
    private static UpdateInfo? RestoreCached(UserSettings settings, UpdateVersion current)
    {
        if (string.IsNullOrEmpty(settings.UpdateAvailableTag) ||
            !UpdateVersion.TryParse(settings.UpdateAvailableTag, out UpdateVersion cached) ||
            cached <= current)
            return null;
        return new UpdateInfo(
            settings.UpdateAvailableTag,
            settings.UpdateAvailableUrl,
            string.IsNullOrEmpty(settings.UpdateAvailableAssetName) ? null : settings.UpdateAvailableAssetName,
            string.IsNullOrEmpty(settings.UpdateAvailableAssetUrl) ? null : settings.UpdateAvailableAssetUrl);
    }

    /// <summary>Writes the check timestamp + result cache into the settings file. Merged over a fresh
    /// load so concurrent non-update fields aren't clobbered; best-effort like all settings writes.</summary>
    private static void PersistResult(UpdateInfo? info)
    {
        UserSettings fresh = UserSettingsFile.Load();
        UserSettingsFile.Save(fresh with
        {
            UpdateLastCheckedUtc = DateTime.UtcNow.ToString("o"),
            UpdateAvailableTag = info?.TagName ?? "",
            UpdateAvailableUrl = info?.HtmlUrl ?? "",
            UpdateAvailableAssetName = info?.AssetName ?? "",
            UpdateAvailableAssetUrl = info?.AssetUrl ?? "",
        });
    }

    private void SetAvailable(UpdateInfo? info)
    {
        Available = info;
        StateChanged?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed.Cancel();
        _http.Dispose();
        _disposed.Dispose();
    }
}
