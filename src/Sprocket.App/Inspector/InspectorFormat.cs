using System.Globalization;
using Sprocket.Core.Model;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure formatting for the Inspector (PLAN.md step 16), split out like <see cref="Timeline.TimelineMath"/> so
/// the value-display logic is unit-testable without an Avalonia surface (the App is a UI-bound WinExe).
/// </summary>
public static class InspectorFormat
{
    /// <summary>
    /// Formats a parameter value for display: up to three decimals, trailing zeros trimmed, with an optional
    /// unit suffix (degrees and percent abut the number; other units are spaced, e.g. <c>"+1 EV"</c> style —
    /// sign is the caller's value, we don't force a <c>+</c>).
    /// <para>
    /// <paramref name="displayScale"/> is <see cref="EffectParameterDescriptor.DisplayScale"/>: a 0–1 ratio
    /// shown as a percentage passes 100, so Opacity 0.5 reads <c>"50%"</c>. The scale applies to the
    /// <em>displayed number only</em> — the slider, the clamp and every command stay in model units.
    /// </para>
    /// </summary>
    public static string Value(double value, string? unit = null, double displayScale = 1.0)
    {
        // Scaling reintroduces binary-float noise the model value didn't have (0.07 * 100 = 7.000000000000001);
        // "0.###" already collapses it, so no extra rounding is needed here.
        string number = (value * displayScale).ToString("0.###", CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(unit))
            return number;
        return unit is "°" or "%" ? $"{number}{unit}" : $"{number} {unit}";
    }

    /// <summary>
    /// Parses a numeric-box entry, accepting the same shapes <see cref="Value"/> produces: a bare number, or
    /// a number followed by the parameter's <paramref name="unit"/> suffix (with or without a space, any
    /// case). Without this, committing back a displayed value like <c>"1.5 EV"</c> or <c>"90°"</c> fails a
    /// plain <see cref="double.TryParse(string?, out double)"/> and the edit silently reverts. As a last
    /// resort the leading numeric token is parsed, so <c>"12 semitones"</c> still commits 12.
    /// <para>
    /// <paramref name="displayScale"/> is the inverse of <see cref="Value"/>'s: the parsed number is divided
    /// by it, so on a percent parameter both <c>"50"</c> and <c>"50%"</c> commit the model value 0.5.
    /// </para>
    /// </summary>
    public static bool TryParseValue(string? text, string? unit, out double value, double displayScale = 1.0)
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
        {
            value /= displayScale;
            return true;
        }

        // Leading numeric token ("12 semitones", "1.5EV" with an unknown suffix…).
        int end = 0;
        while (end < trimmed.Length &&
               (char.IsAsciiDigit(trimmed[end]) || trimmed[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            end++;
        }
        if (end == 0 ||
            !double.TryParse(trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }
        value /= displayScale;
        return true;
    }
}
