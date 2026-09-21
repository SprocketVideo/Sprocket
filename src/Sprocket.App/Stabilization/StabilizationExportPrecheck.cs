using System;
using System.Collections.Generic;
using Sprocket.Core.Model;

namespace Sprocket.App.Stabilization;

/// <summary>
/// The export pre-check for stabilization (plan/features/stabilization.md phase 6): which stabilized clips do not
/// yet have a ready analysis, so an export would render them unstabilized. Pure and testable — the status lookup is
/// injected (the real one is <see cref="StabilizationService.StatusOf"/>). One entry per distinct (source, detail),
/// carrying a representative clip for its used source range.
/// </summary>
public static class StabilizationExportPrecheck
{
    /// <summary>The stabilized clips whose analysis is not <see cref="AnalysisState.Ready"/>, de-duplicated by
    /// (source, detail). Empty when every stabilized clip is analyzed (the export happy path).</summary>
    public static IReadOnlyList<StabilizationScan.Item> Unanalyzed(
        Project project, Func<MediaRefId, bool, AnalysisState> statusOf)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(statusOf);

        var seen = new HashSet<(Guid, bool)>();
        var result = new List<StabilizationScan.Item>();
        foreach (StabilizationScan.Item item in StabilizationScan.StabilizedClips(project))
        {
            if (statusOf(item.Media.Id, item.Detailed) == AnalysisState.Ready)
                continue;
            if (seen.Add((item.Media.Id.Value, item.Detailed)))
                result.Add(item);
        }
        return result;
    }
}
