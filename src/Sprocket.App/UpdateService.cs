using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Sprocket.App;

/// <summary>
/// The app-scoped update service (PLAN.md steps 36 + 45), built on Velopack: for installed builds
/// (Windows Setup, Linux AppImage, macOS .app — anything `vpk pack` produced) it checks the GitHub
/// releases feed for this build's channel, downloads the update, and applies it with a restart. A
/// portable-zip or dev run is not a Velopack install (<see cref="IsInstalled"/> is false) — those
/// builds can't self-update, and the UI points at the releases page instead. Modeled on
/// <see cref="McpServerService"/>: constructed by <see cref="App"/>, it outlives window swaps and the
/// shell mirrors its state into a status-bar badge. All members are meant to be used from the UI
/// thread (calls are awaited from it, so continuations land back there).
/// </summary>
internal sealed class UpdateService
{
    /// <summary>Where releases are published — also the feed <see cref="Velopack.Sources.GithubSource"/>
    /// reads (the per-channel <c>releases.&lt;rid&gt;.json</c> assets uploaded by the release workflow).</summary>
    public const string RepoUrl = "https://github.com/SprocketVideo/Sprocket";

    /// <summary>The human releases page, for portable/dev builds that can't self-update.</summary>
    public const string ReleasesPageUrl = RepoUrl + "/releases";

    /// <summary>What a <see cref="CheckAsync"/> call concluded, for Help ▸ Check for Updates feedback.</summary>
    public enum Outcome
    {
        /// <summary>Automatic checks are switched off in Preferences (auto path only).</summary>
        Disabled,

        /// <summary>Not a Velopack install (portable zip / dev run) — self-update is unavailable.</summary>
        NotInstalled,

        /// <summary>A newer release exists for this channel (see <see cref="AvailableVersion"/>).</summary>
        UpdateAvailable,

        /// <summary>The feed held nothing newer for this channel.</summary>
        UpToDate,

        /// <summary>The check could not run (network/feed failure — see <see cref="LastError"/>).</summary>
        Failed,
    }

    private readonly UpdateManager? _manager; // null = not a Velopack install; self-update unavailable
    private UpdateInfo? _update;              // the pending update CheckAsync found, fed to Download/Apply

    /// <summary>Version string of the newer release found ("0.1.64-alpha"), or <see langword="null"/>
    /// when up to date, disabled, not installed, or never checked.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>The failure message when the last check <see cref="Outcome.Failed"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether this process is a Velopack-managed install that can self-update.</summary>
    public bool IsInstalled => _manager is not null;

    /// <summary>Raised (on the UI thread) after <see cref="AvailableVersion"/> may have changed.</summary>
    public event Action? StateChanged;

    public UpdateService()
    {
        try
        {
            // Prereleases included deliberately: the whole alpha ships as GitHub prereleases. The channel
            // (win-x64 / osx-arm64 / …) was baked into the install by `vpk pack --channel <rid>`, so the
            // manager only ever offers same-platform builds.
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: true));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            // Defensive (§15): a broken install metadata state must not stop the editor from starting.
            CrashLog.Write("Velopack UpdateManager init failed", ex);
            _manager = null;
        }
    }

    /// <summary>
    /// Runs one check. The automatic path (<paramref name="force"/> false) honours the Preferences
    /// enable switch; Help ▸ Check for Updates passes <see langword="true"/> to bypass it. Network
    /// failures leave any previously shown result in place.
    /// </summary>
    public async Task<Outcome> CheckAsync(UserSettings settings, bool force)
    {
        if (!force && !settings.UpdateCheckEnabled)
        {
            SetAvailable(null);
            return Outcome.Disabled;
        }

        if (_manager is null)
            return Outcome.NotInstalled;

        UpdateInfo? update;
        try
        {
            update = await _manager.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            LastError = "Could not reach github.com to check for updates.";
            CrashLog.Write("Update check failed", ex);
            return Outcome.Failed; // keep whatever was already shown; try again next launch
        }

        LastError = null;
        _update = update;
        SetAvailable(update?.TargetFullRelease.Version.ToString());
        return update is null ? Outcome.UpToDate : Outcome.UpdateAvailable;
    }

    /// <summary>Downloads the update <see cref="CheckAsync"/> found (delta when possible, full
    /// otherwise). <paramref name="progress"/> is reported 0–100 on a worker thread.</summary>
    public Task DownloadAsync(Action<int> progress)
    {
        if (_manager is null || _update is null)
            throw new InvalidOperationException("No pending update to download.");
        return _manager.DownloadUpdatesAsync(_update, progress);
    }

    /// <summary>Applies the downloaded update and restarts the app. Does not return on success —
    /// the process exits so the updater can swap the install.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _update is null)
            throw new InvalidOperationException("No downloaded update to apply.");
        _manager.ApplyUpdatesAndRestart(_update);
    }

    private void SetAvailable(string? version)
    {
        AvailableVersion = version;
        StateChanged?.Invoke();
    }
}
