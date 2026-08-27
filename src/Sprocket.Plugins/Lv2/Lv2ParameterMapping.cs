using System.Globalization;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Lv2;

/// <summary>
/// Maps an LV2 plugin's Turtle port descriptions onto Sprocket's type-driven effect model (PLAN.md step 59):
/// each control-input port becomes a typed <see cref="EffectParameterDescriptor"/> keyed by its stable
/// <c>lv2:symbol</c>, and the plugin becomes an <see cref="EffectCategory.Audio"/> <see cref="EffectDescriptor"/>
/// (routed to the mixer chain; controls built by the Inspector). Pure — unit-tested against hand-built
/// <see cref="Lv2PluginInfo"/>s.
/// </summary>
/// <remarks>
/// <c>lv2:sampleRate</c> bounds are resolved against a nominal 48 kHz for the slider only; the stored value is
/// what the plugin receives, as for LADSPA. An <c>lv2:enumeration</c> whose scale points are exactly
/// <c>0 … n-1</c> renders as a <see cref="ParameterKind.Dropdown"/> (the value <em>is</em> the index); any other
/// enumeration falls back to an integer slider, since a dropdown stores its index, not an arbitrary value.
/// </remarks>
internal static class Lv2ParameterMapping
{
    public const double NominalSampleRate = 48000.0;

    /// <summary>The effect-type-id prefix every LV2-hosted effect shares; the plugin URI follows it verbatim.</summary>
    public const string IdPrefix = "plugin.lv2.";

    public static string EffectId(Lv2PluginInfo info) => IdPrefix + info.Uri;

    /// <summary>The stable parameter key for a control port: its symbol (LV2 guarantees symbols are stable and
    /// unique within a plugin), or <c>port&lt;index&gt;</c> when a bundle omits one.</summary>
    public static string ParameterName(Lv2PortInfo port) =>
        string.IsNullOrWhiteSpace(port.Symbol) ? "port" + port.Index.ToString(CultureInfo.InvariantCulture) : port.Symbol;

    public static EffectDescriptor ToEffectDescriptor(Lv2PluginInfo info)
    {
        var parameters = new List<EffectParameterDescriptor>();
        foreach (Lv2PortInfo port in info.ControlInputs)
        {
            if (port.Properties.Contains(Lv2Ns.PortPropsNotOnGui))
                continue; // the plugin asks hosts not to show this control; it keeps its default
            parameters.Add(ToParameter(port));
        }

        string displayName = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : info.Uri;
        string description = !string.IsNullOrWhiteSpace(info.Comment) ? FirstLine(info.Comment)
            : info.Maker.Length > 0 ? $"LV2 plugin by {info.Maker}."
            : "LV2 plugin.";

        return new EffectDescriptor(EffectId(info), displayName, EffectCategory.Audio, description, parameters);
    }

    public static EffectParameterDescriptor ToParameter(Lv2PortInfo port)
    {
        string name = ParameterName(port);
        string display = port.Name.Length > 0 ? port.Name : name;
        string? unit = UnitSuffix(port.UnitIri);
        string? description = string.IsNullOrWhiteSpace(port.Comment) ? null : FirstLine(port.Comment);

        if (port.Properties.Contains(Lv2Ns.Toggled))
        {
            double toggleDefault = (port.Default ?? 0.0) >= 0.5 ? 1.0 : 0.0;
            return new EffectParameterDescriptor(name, display, toggleDefault, 0.0, 1.0, 1.0, null, description, ParameterKind.Toggle);
        }

        bool integer = port.Properties.Contains(Lv2Ns.Integer);
        bool enumeration = port.Properties.Contains(Lv2Ns.Enumeration);
        double scale = port.Properties.Contains(Lv2Ns.SampleRate) ? NominalSampleRate : 1.0;

        if (enumeration && port.ScalePoints.Count > 0 && IsIndexEnumeration(port.ScalePoints, out string[] labels))
        {
            double def = Math.Clamp(Math.Round(port.Default ?? 0.0), 0, labels.Length - 1);
            return new EffectParameterDescriptor(name, display, def, 0.0, labels.Length - 1, 1.0, null, description,
                ParameterKind.Dropdown, labels);
        }

        double? lower = port.Minimum * scale;
        double? upper = port.Maximum * scale;
        if (enumeration && port.ScalePoints.Count > 0)
        {
            // A non-index enumeration: bound the slider by its scale points when the bundle gives no range.
            lower ??= port.ScalePoints.Min(s => s.Value);
            upper ??= port.ScalePoints.Max(s => s.Value);
            integer = integer || port.ScalePoints.All(s => s.Value == Math.Floor(s.Value));
        }

        double defaultValue = port.Default ?? lower ?? 0.0;
        (double min, double max) = ResolveRange(lower, upper, defaultValue);
        defaultValue = Math.Clamp(defaultValue, min, max);
        if (integer)
            defaultValue = Math.Round(defaultValue);

        double step = integer ? 1.0 : Math.Max((max - min) / 100.0, 1e-4);
        return new EffectParameterDescriptor(name, display, defaultValue, min, max, step, unit, description,
            integer ? ParameterKind.Integer : ParameterKind.Continuous);
    }

    /// <summary>Whether the scale points are exactly the values 0 … n-1 (in any order); returns their labels by value.</summary>
    private static bool IsIndexEnumeration(IReadOnlyList<Lv2ScalePoint> points, out string[] labels)
    {
        labels = new string[points.Count];
        var seen = new bool[points.Count];
        foreach (Lv2ScalePoint p in points)
        {
            if (p.Value != Math.Floor(p.Value) || p.Value < 0 || p.Value >= points.Count)
                return false;
            int i = (int)p.Value;
            if (seen[i])
                return false;
            seen[i] = true;
            labels[i] = p.Label;
        }
        return true;
    }

    /// <summary>Invents a finite slider range when a bound is missing (LV2 bounds are optional).</summary>
    private static (double Min, double Max) ResolveRange(double? lower, double? upper, double defaultValue)
    {
        double min = lower ?? Math.Min(0.0, Math.Min(defaultValue, (upper ?? 1.0) - 1.0));
        double max = upper ?? Math.Max(1.0, Math.Max(defaultValue * 2.0, min + 1.0));
        if (max <= min)
            max = min + 1.0;
        return (min, max);
    }

    /// <summary>The display suffix for a <c>units:</c> unit IRI (null for none / unknown).</summary>
    public static string? UnitSuffix(string? unitIri)
    {
        if (unitIri is null || !unitIri.StartsWith(Lv2Ns.Units, StringComparison.Ordinal))
            return null;
        return unitIri[Lv2Ns.Units.Length..] switch
        {
            "db" => "dB",
            "hz" => "Hz",
            "khz" => "kHz",
            "mhz" => "MHz",
            "ms" => "ms",
            "s" => "s",
            "min" => "min",
            "pc" => "%",
            "semitone12TET" => "st",
            "cent" => "ct",
            "degree" => "°",
            "bpm" => "BPM",
            "m" => "m",
            "cm" => "cm",
            "mm" => "mm",
            "km" => "km",
            "inch" => "in",
            "mile" => "mi",
            "oct" => "oct",
            "bar" => "bars",
            "beat" => "beats",
            "frame" => "frames",
            _ => null,
        };
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(['\r', '\n']);
        return (nl >= 0 ? text[..nl] : text).Trim();
    }
}
