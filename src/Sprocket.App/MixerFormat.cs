using System.Globalization;

namespace Sprocket.App;

/// <summary>
/// Pure formatting for the audio mixer / loudness meters (PLAN.md step 30, UI.md §3.3): gain and pan labels,
/// LUFS / dBTP read-outs, and the 0–1 fill fraction that drives a meter bar. Kept free of any Avalonia control —
/// like <see cref="StatusBarFormat"/> / <see cref="MarkerListFormat"/> — so the strings/fractions are unit-testable
/// and the mixer control only maps them onto widgets.
/// </summary>
public static class MixerFormat
{
    private const string NegInf = "-∞"; // silence sentinel

    /// <summary>The bottom of the fader's travel, at which a level reads as silence (<c>"-∞ dB"</c>).</summary>
    public const double SilenceFloorDb = -60.0;

    /// <summary>A gain in dB as a signed, one-decimal label (e.g. <c>"+3.0 dB"</c>, <c>"-6.0 dB"</c>,
    /// <c>"0.0 dB"</c>); values at/below <see cref="SilenceFloorDb"/> read as <c>"-∞ dB"</c>.</summary>
    public static string GainDbLabel(double db)
    {
        if (double.IsNegativeInfinity(db) || db <= SilenceFloorDb)
            return $"{NegInf} dB";
        return $"{Signed(db)} dB";
    }

    /// <summary>A pan/balance value in [-1, 1] as <c>"C"</c> (centre), <c>"L100".."L1"</c> (left) or
    /// <c>"R1".."R100"</c> (right).</summary>
    public static string PanLabel(double pan)
    {
        pan = Math.Clamp(pan, -1.0, 1.0);
        int pct = (int)Math.Round(Math.Abs(pan) * 100);
        if (pct == 0) return "C";
        return pan < 0 ? $"L{pct}" : $"R{pct}";
    }

    /// <summary>
    /// Parses a typed gain entry back to dB, accepting everything <see cref="GainDbLabel"/> renders plus the
    /// bare numbers a user is likeliest to type: <c>"-6"</c>, <c>"-6.0 dB"</c>, <c>"+3dB"</c>, <c>"0"</c>, and
    /// the silence sentinel in any of its spellings (<c>"-∞"</c>, <c>"-inf"</c>, <c>"-infinity"</c>, with or
    /// without a <c>dB</c> suffix) which maps to the <see cref="SilenceFloorDb"/> fader floor. Anything else
    /// is rejected so a typo reverts the field rather than silently muting a track.
    /// </summary>
    public static bool TryParseGainDb(string? text, out double db)
    {
        db = 0.0;
        string trimmed = StripSuffix(text, "dB");
        if (trimmed.Length == 0)
            return false;

        // "-∞" / "-inf" / "-infinity": the label's silence sentinel, committed as the fader floor rather than
        // double.NegativeInfinity so the value stays inside the slider's range and round-trips through undo.
        if (trimmed.ToLowerInvariant() is NegInf or "-inf" or "-infinity")
        {
            db = SilenceFloorDb;
            return true;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            double.IsNaN(parsed))
        {
            return false;
        }
        db = double.IsNegativeInfinity(parsed) ? SilenceFloorDb : parsed;
        return true;
    }

    /// <summary>
    /// Parses a typed pan entry back to the model's [-1, 1], accepting everything <see cref="PanLabel"/>
    /// renders — <c>"C"</c>, <c>"L50"</c>, <c>"R25"</c> (any case) — plus a bare signed -100..100, which is
    /// how Premiere's pan field reads. Anything else is rejected so the field reverts.
    /// </summary>
    public static bool TryParsePan(string? text, out double pan)
    {
        pan = 0.0;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Equals("C", StringComparison.OrdinalIgnoreCase))
            return true;

        // "L50" / "R25" — the side letter carries the sign, so the magnitude that follows is unsigned.
        char side = char.ToUpperInvariant(trimmed[0]);
        if (side is 'L' or 'R')
        {
            if (!double.TryParse(trimmed[1..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double magnitude) ||
                magnitude < 0)
            {
                return false;
            }
            pan = Math.Clamp(magnitude / 100.0, 0.0, 1.0) * (side == 'L' ? -1 : 1);
            return true;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) ||
            double.IsNaN(percent))
        {
            return false;
        }
        pan = Math.Clamp(percent / 100.0, -1.0, 1.0);
        return true;
    }

    /// <summary>A loudness value as a one-decimal LUFS label (<c>"-14.2 LUFS"</c>), or <c>"-∞ LUFS"</c> for
    /// silence / not-yet-measured.</summary>
    public static string LufsLabel(double lufs) =>
        double.IsNegativeInfinity(lufs) || double.IsNaN(lufs) ? $"{NegInf} LUFS" : $"{lufs:0.0} LUFS";

    /// <summary>A true-peak value as a one-decimal dBTP label (<c>"-1.0 dBTP"</c>), or <c>"-∞ dBTP"</c> for
    /// silence.</summary>
    public static string DbtpLabel(double dbtp) =>
        double.IsNegativeInfinity(dbtp) || double.IsNaN(dbtp) ? $"{NegInf} dBTP" : $"{dbtp:0.0} dBTP";

    /// <summary>
    /// Maps a level in dB(FS) to a 0–1 meter fill between <paramref name="floorDb"/> (0) and
    /// <paramref name="ceilingDb"/> (1); silence and anything at/below the floor is 0, anything at/above the
    /// ceiling is 1.
    /// </summary>
    public static double MeterFillFraction(double db, double floorDb = -60.0, double ceilingDb = 0.0)
    {
        if (double.IsNegativeInfinity(db) || double.IsNaN(db) || db <= floorDb) return 0.0;
        if (db >= ceilingDb) return 1.0;
        return (db - floorDb) / (ceilingDb - floorDb);
    }

    /// <summary>Trims the input and drops a trailing unit suffix (any case, with or without a space), so a
    /// value read straight off the label parses back.</summary>
    private static string StripSuffix(string? text, string suffix)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length].TrimEnd()
            : trimmed;
    }

    private static string Signed(double value)
    {
        // Round to one decimal first so a tiny negative doesn't render as "-0.0".
        double r = Math.Round(value, 1);
        return (r > 0 ? "+" : r < 0 ? "-" : "") + Math.Abs(r).ToString("0.0");
    }
}
