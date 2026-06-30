using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// Generates fresh, unique sequence display names (PLAN.md step 23). Mirrors the convention in leading editors
/// (Premiere's "Sequence 01" on create, "Nested Sequence 01" on nest): pick the first <c>"{prefix} N"</c> that is
/// not already taken, case-insensitively. Pure naming policy, so it sits in the App alongside the menu wiring.
/// </summary>
public static class SequenceNaming
{
    /// <summary>The first <c>"{prefix} N"</c> (N starting at 1) not already used by a sequence in the project.</summary>
    public static string NextUnique(Project project, string prefix)
    {
        ArgumentNullException.ThrowIfNull(project);
        var taken = new HashSet<string>(project.Sequences.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = $"{prefix} {n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }
}
