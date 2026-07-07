using System.Globalization;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure formatting for the Inspector (PLAN.md step 16), split out like <see cref="Timeline.TimelineMath"/> so
/// the value-display logic is unit-testable without an Avalonia surface (the App is a UI-bound WinExe).
/// </summary>
public static class InspectorFormat
{
    /// <summary>
    /// Formats a parameter value for display: up to three decimals, trailing zeros trimmed, with an optional
    /// unit suffix (degrees abut the number; other units are spaced, e.g. <c>"+1 EV"</c> style — sign is the
    /// caller's value, we don't force a <c>+</c>).
    /// </summary>
    public static string Value(double value, string? unit = null)
    {
        string number = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(unit))
            return number;
        return unit == "°" ? $"{number}{unit}" : $"{number} {unit}";
    }

    /// <summary>
    /// Parses a numeric-box entry, accepting the same shapes <see cref="Value"/> produces: a bare number, or
    /// a number followed by the parameter's <paramref name="unit"/> suffix (with or without a space, any
    /// case). Without this, committing back a displayed value like <c>"1.5 EV"</c> or <c>"90°"</c> fails a
    /// plain <see cref="double.TryParse(string?, out double)"/> and the edit silently reverts. As a last
    /// resort the leading numeric token is parsed, so <c>"12 semitones"</c> still commits 12.
    /// </summary>
    public static bool TryParseValue(string? text, string? unit, out double value)
    {
        value = 0.0;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return false;

        if (!string.IsNullOrEmpty(unit) &&
            trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^unit.Length].TrimEnd();
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        // Leading numeric token ("12 semitones", "1.5EV" with an unknown suffix…).
        int end = 0;
        while (end < trimmed.Length &&
               (char.IsAsciiDigit(trimmed[end]) || trimmed[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            end++;
        }
        return end > 0 &&
               double.TryParse(trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
