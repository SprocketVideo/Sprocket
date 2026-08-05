using System;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// Builds the undoable edits behind the Proxy dialog's two settings — the on/off toggle and the resolution tier
/// (PLAN.md step 18). Both live in the project file (<see cref="ProjectSettings.UseProxies"/> /
/// <see cref="ProjectSettings.ProxyTier"/>), so neither may be a direct assignment: a direct set would leave the
/// document clean and the change would silently vanish on the next reload.
/// </summary>
/// <remarks>
/// Each is a <see cref="SetPropertyCommand{T}"/> whose setter does <em>both</em> halves — mutate the model and
/// drive the live service transition. Because <c>Apply</c> and <c>Revert</c> run the same setter, undo/redo
/// reconfigures the running service for free, and routing through <see cref="EditHistory"/> is what marks the
/// document dirty so autosave/save persist it. Kept out of the window so the decisions are unit-testable headlessly
/// (the App is a UI-bound WinExe), in the shape of <see cref="SequenceSettingsOps"/>.
/// </remarks>
public static class ProxySettingsOps
{
    /// <summary>
    /// The command turning proxies on or off for <paramref name="settings"/>, also applying it to
    /// <paramref name="apply"/> (the live service) as it goes, or <see langword="null"/> when unchanged.
    /// </summary>
    public static IEditCommand? BuildEnableCommand(ProjectSettings settings, Action<bool> apply, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(apply);
        if (settings.UseProxies == enabled)
            return null;

        return SetPropertyCommand<bool>.Create(
            enabled ? "Enable proxies" : "Disable proxies",
            () => settings.UseProxies,
            v => { settings.UseProxies = v; apply(v); },
            enabled);
    }

    /// <summary>
    /// The command changing the proxy resolution tier for <paramref name="settings"/>, also applying it to
    /// <paramref name="apply"/> (the live service) as it goes, or <see langword="null"/> when unchanged. Changing
    /// the tier re-keys the proxy cache, so applying it rebuilds — the dialog warns before this is called.
    /// </summary>
    public static IEditCommand? BuildTierCommand(ProjectSettings settings, Action<ProxyTier> apply, ProxyTier tier)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(apply);
        if (settings.ProxyTier == tier)
            return null;

        return SetPropertyCommand<ProxyTier>.Create(
            "Change proxy resolution",
            () => settings.ProxyTier,
            v => { settings.ProxyTier = v; apply(v); },
            tier);
    }

    /// <summary>The dialog's label for a tier, naming the fraction of the source it targets (all capped at the
    /// 1080p preview ceiling, <see cref="ProxyPolicy.CeilingWidth"/>). Pure, so the wording is testable.</summary>
    public static string TierLabel(ProxyTier tier) => tier switch
    {
        ProxyTier.Quarter => "Quarter (¼ · fastest)",
        ProxyTier.Half => "Half (½ · default)",
        ProxyTier.FullHd => "Full (up to 1080p)",
        _ => tier.ToString(),
    };
}
