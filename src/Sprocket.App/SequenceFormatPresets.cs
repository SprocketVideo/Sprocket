using System;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// The sequence frame-size presets offered by the New Sequence / Sequence Settings dialog
/// (<see cref="SequenceSettingsDialog"/>): the standard landscape deliveries plus the portrait / square
/// social formats leading editors ship as sequence presets, with "Custom" (free width × height entry) last.
/// Pure lookup + validation helpers, kept out of the dialog so they are unit-testable headlessly.
/// </summary>
public static class SequenceFormatPresets
{
    /// <summary>The preset choices in dropdown order; the value is <see langword="null"/> for "Custom".</summary>
    public static readonly (string Label, Resolution? Value)[] Presets =
    [
        ("1920 × 1080 (1080p)", new Resolution(1920, 1080)),
        ("1080 × 1920 (1080p Portrait)", new Resolution(1080, 1920)),
        ("1080 × 1350 (4:5 Portrait)", new Resolution(1080, 1350)),
        ("1080 × 1080 (Square)", new Resolution(1080, 1080)),
        ("3840 × 2160 (4K UHD)", new Resolution(3840, 2160)),
        ("2160 × 3840 (4K Portrait)", new Resolution(2160, 3840)),
        ("Custom", null),
    ];

    /// <summary>The index of the "Custom" entry (always the last).</summary>
    public static int CustomIndex => Presets.Length - 1;

    /// <summary>Frame-size bounds accepted for a custom format (matches what decode/encode handle sanely).</summary>
    public const int MinDimension = 16;

    /// <inheritdoc cref="MinDimension"/>
    public const int MaxDimension = 8192;

    /// <summary>
    /// The dropdown index whose preset equals <paramref name="resolution"/>, or <see cref="CustomIndex"/>
    /// when no preset matches (the seeded sequence has a hand-entered size).
    /// </summary>
    public static int IndexOf(Resolution resolution)
    {
        for (int i = 0; i < Presets.Length; i++)
            if (Presets[i].Value is { } value && value == resolution)
                return i;
        return CustomIndex;
    }

    /// <summary>
    /// Parses the custom width/height boxes: both must be integers in
    /// [<see cref="MinDimension"/>, <see cref="MaxDimension"/>]. Returns <see langword="false"/> (and a
    /// default <paramref name="resolution"/>) on any malformed or out-of-range input.
    /// </summary>
    public static bool TryParse(string? widthText, string? heightText, out Resolution resolution)
    {
        resolution = default;
        if (!int.TryParse((widthText ?? string.Empty).Trim(), out int width)
            || !int.TryParse((heightText ?? string.Empty).Trim(), out int height))
            return false;
        if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
            return false;
        resolution = new Resolution(width, height);
        return true;
    }
}
