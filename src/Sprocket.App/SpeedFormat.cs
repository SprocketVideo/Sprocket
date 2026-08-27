using System;
using System.Globalization;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure conversions between a clip's playback <see cref="Rational"/> speed and the percentage shown in the
/// UI (retime, PLAN.md step 21) — 100% = normal, 150% = 3/2, 50% = 1/2. Split out and unit-tested like
/// <see cref="Timeline.TimelineMath"/> / <see cref="ClipboardOps"/>; the Speed dialog and the Inspector
/// speed row both route through it. Avalonia-free.
/// </summary>
public static class SpeedFormat
{
    /// <summary>Formats a speed ratio as its percentage string (no trailing zeros), e.g. 3/2 → "150".</summary>
    public static string ToPercentString(Rational speed) =>
        (speed.ToDouble() * 100).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a positive percentage into an exact speed ratio (e.g. "150" → 3/2, "100" → 1/1). Returns
    /// <see langword="false"/> (and leaves <paramref name="speed"/> at 1/1) when the text is not a positive
    /// number — reverse and freeze (0%) are deferred, so non-positive input is rejected.
    /// </summary>
    public static bool TryParsePercent(string? text, out Rational speed)
    {
        speed = Rational.One;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) || pct <= 0)
            return false;
        // Keep two decimals of the percentage as an exact fraction over 10000, reduced (100% → 1/1, 150% → 3/2).
        int num = (int)Math.Round(pct * 100);
        if (num <= 0)
            return false;
        speed = new Rational(num, 10000);
        return true;
    }

    /// <summary>
    /// Converts a speed given as a fraction of normal (1.0 = 100%, the unit the keyframed speed lane and
    /// <see cref="Sprocket.Core.Model.SpeedRamp"/> use) into the exact ratio a constant-speed clip stores, keeping
    /// two decimals of the percentage (0.5 → 1/2, 1.5 → 3/2). Non-positive / NaN input clamps to the ramp's
    /// minimum speed rather than failing, since a slider can't express "invalid".
    /// </summary>
    public static Rational FromFraction(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0)
            fraction = Sprocket.Core.Model.SpeedRamp.MinSpeed;
        int num = Math.Max(1, (int)Math.Round(fraction * 10000));
        return new Rational(num, 10000);
    }
}
