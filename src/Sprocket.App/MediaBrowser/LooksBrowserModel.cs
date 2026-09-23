using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Render;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// The Looks tab's pure helpers (plan/features/looks-browser.md), split out of <see cref="MediaBrowserPanel"/> so they
/// are headlessly testable: the row badge, the search filter, the group order, the look an imported creative LUT
/// becomes, the status line an apply reports — and <see cref="Apply"/>, the one apply path the browser's double-click
/// and the timeline's drop share.
/// </summary>
internal static class LooksBrowserModel
{
    /// <summary>The badge on a look's row: <c>"LUT"</c> for a single creative-LUT look, otherwise the effect count.</summary>
    public static string Badge(Look look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (look.Entries is [{ EffectTypeId: EffectTypeIds.CreativeLut }])
            return "LUT";
        int n = look.Entries.Count;
        return n == 1 ? "1 effect" : $"{n} effects";
    }

    /// <summary>Whether <paramref name="look"/> matches the search box: its name, group or description contains every
    /// whitespace-separated term (case-insensitive). A blank search matches everything.</summary>
    public static bool Matches(Look look, string? search)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (string.IsNullOrWhiteSpace(search))
            return true;
        string haystack = $"{look.Name} {look.Group} {look.Description}";
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The looks to list, grouped: the built-in groups in <see cref="LooksCatalog.Groups"/> order, then
    /// <see cref="Look.UserGroup"/> — each with its matching looks in catalog / save order. Empty groups are
    /// omitted, except that the user group is always present so its empty-state hint can show.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<Look> Looks)> Grouped(IReadOnlyList<Look> all, string? search)
    {
        ArgumentNullException.ThrowIfNull(all);
        var groups = new List<(string, IReadOnlyList<Look>)>();
        foreach (string group in LooksCatalog.Groups.Append(Look.UserGroup))
        {
            List<Look> looks = [.. all.Where(l => l.Group == group && Matches(l, search))];
            if (looks.Count > 0 || group == Look.UserGroup)
                groups.Add((group, looks));
        }
        return groups;
    }

    /// <summary>The one-entry look an imported creative <c>.cube</c> becomes (Resolve's LUT browser / Premiere's
    /// Creative "Look" browse): the Creative LUT effect at full Intensity, named after the file.</summary>
    public static Look FromLutFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string name = Path.GetFileNameWithoutExtension(path);
        return new Look(
            Look.NewUserId(),
            string.IsNullOrWhiteSpace(name) ? "LUT" : name,
            Look.UserGroup,
            $"Creative LUT: {Path.GetFileName(path)}",
            [
                new LookEntry(EffectTypeIds.CreativeLut,
                    new Dictionary<string, double> { [EffectParamNames.Mix] = 1.0 },
                    new Dictionary<string, string> { [EffectParamNames.LutFile] = path }),
            ]);
    }

    /// <summary>The status-strip line after applying <paramref name="look"/> to the clip named <paramref name="clipName"/>
    /// (or failing to), naming any effect types that were skipped because they are not installed.</summary>
    public static string ApplyStatus(Look look, LookApplyResult result, string clipName)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(result);
        string skipped = result.Skipped.Count == 0 ? string.Empty
            : $" Skipped {result.Skipped.Count} unavailable effect{(result.Skipped.Count == 1 ? "" : "s")} ({string.Join(", ", result.Skipped.Distinct())}).";
        if (result.Command is null)
            return $"Couldn't apply look {look.Name}: none of its effects are available.{skipped}";
        return $"Applied look {look.Name} to {clipName}.{skipped}";
    }

    /// <summary>
    /// Applies <paramref name="look"/> to <paramref name="clip"/> as one undoable edit through <paramref name="history"/>
    /// (nothing is executed when no entry survives) and starts loading any creative LUT it references off the render
    /// thread, so the first frame drawn with it does not pay for the parse.
    /// </summary>
    public static LookApplyResult Apply(Look look, Clip clip, EditHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        LookApplyResult result = LookApplication.Build(look, clip);
        if (result.Command is null)
            return result;
        history.Execute(result.Command);
        foreach (EffectInstance effect in result.Added)
            if (effect.Assets.TryGetValue(EffectParamNames.LutFile, out string? lut))
                CreativeLuts.Preload(lut);
        return result;
    }

    /// <summary>The status line when a look is applied to a clip on an audio track.</summary>
    public const string AudioClipRefusal = "Looks grade video. Select a video clip to apply one.";

    /// <summary>
    /// The browser's and the timeline's shared apply: refuses a clip on an audio track (looks carry only colour
    /// effects, which an audio clip never renders — the Inspector filters them the same way through
    /// <see cref="EffectRelevance"/>), otherwise <see cref="Apply"/>s. Returns the status-strip line.
    /// </summary>
    public static string ApplyToClip(Look look, Clip clip, Project project, EditHistory history)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (EffectRelevance.IsOnAudioTrack(project.Timeline, clip))
            return AudioClipRefusal;
        return ApplyStatus(look, Apply(look, clip, history), ClipName(project, clip));
    }

    /// <summary>The display name of <paramref name="clip"/> for a status line: its source file name, else "clip".</summary>
    public static string ClipName(Project project, Clip clip) =>
        Path.GetFileName(project.MediaPool.Get(clip.MediaRefId)?.AbsolutePath) is { Length: > 0 } name ? name : "clip";
}
