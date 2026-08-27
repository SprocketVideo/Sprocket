using System.Globalization;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Frei0r;

/// <summary>
/// Maps a frei0r plugin's parameter table onto Sprocket's type-driven effect model (PLAN.md step 59). frei0r
/// parameters are all normalised to <c>0…1</c>: a <c>BOOL</c> becomes a Toggle, a <c>DOUBLE</c> a Continuous
/// slider, a <c>COLOR</c> three sliders (<c>.r/.g/.b</c>) and a <c>POSITION</c> two (<c>.x/.y</c>) — the same
/// decomposition Kdenlive/Shotcut use for frei0r colours and positions when no bespoke widget exists.
/// <c>STRING</c> parameters have no numeric representation and are left at the plugin's default (noted in the
/// effect description). Pure — unit-tested against hand-built <see cref="Frei0rPluginInfo"/>s.
/// </summary>
/// <remarks>Parameter keys are <c>p&lt;index&gt;</c> (+ component suffix) because the index is what the ABI
/// addresses and frei0r names are free text; the effect id is <c>plugin.frei0r.&lt;library-file-name&gt;</c>.</remarks>
internal static class Frei0rParameterMapping
{
    public const string IdPrefix = "plugin.frei0r.";

    public static string EffectId(Frei0rPluginInfo info) => IdPrefix + Sanitize(info.Key);

    public static string ParameterName(int index, string? component = null) =>
        "p" + index.ToString(CultureInfo.InvariantCulture) + (component is null ? "" : "." + component);

    public static EffectDescriptor ToEffectDescriptor(Frei0rPluginInfo info)
    {
        var parameters = new List<EffectParameterDescriptor>();
        bool hasStringParam = false;
        foreach (Frei0rParamInfo p in info.Params)
        {
            if (p.Type == Frei0rAbi.ParamString)
                hasStringParam = true;
            parameters.AddRange(ToParameters(p));
        }

        string description = !string.IsNullOrWhiteSpace(info.Explanation) ? info.Explanation.Trim()
            : info.Author.Length > 0 ? $"frei0r filter by {info.Author}."
            : "frei0r filter.";
        description += " CPU effect — heavy in playback; render-cache the range.";
        if (hasStringParam)
            description += " (Text parameters keep the plugin's defaults.)";

        string displayName = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : info.Key;
        return new EffectDescriptor(EffectId(info), displayName, EffectCategory.Video, description, parameters);
    }

    /// <summary>The descriptor(s) for one frei0r parameter (0–3 of them, by type). Defaults come from the plugin's own
    /// constructed-instance values when known (<see cref="Frei0rParamInfo.Defaults"/>), else the generic 0 / 0.5 / white
    /// / centre.</summary>
    public static IEnumerable<EffectParameterDescriptor> ToParameters(Frei0rParamInfo p)
    {
        string? tooltip = string.IsNullOrWhiteSpace(p.Explanation) ? null : p.Explanation.Trim();
        double Default(int component, double fallback) =>
            p.Defaults is { } d && component < d.Count ? d[component] : fallback;

        switch (p.Type)
        {
            case Frei0rAbi.ParamBool:
                yield return new EffectParameterDescriptor(ParameterName(p.Index), p.Name, Default(0, 0.0) >= 0.5 ? 1.0 : 0.0, 0.0, 1.0, 1.0, null, tooltip, ParameterKind.Toggle);
                break;

            case Frei0rAbi.ParamDouble:
                yield return new EffectParameterDescriptor(ParameterName(p.Index), p.Name, Default(0, 0.5), 0.0, 1.0, 0.01, null, tooltip);
                break;

            case Frei0rAbi.ParamColor:
                yield return new EffectParameterDescriptor(ParameterName(p.Index, "r"), p.Name + " R", Default(0, 1.0), 0.0, 1.0, 0.01, null, tooltip);
                yield return new EffectParameterDescriptor(ParameterName(p.Index, "g"), p.Name + " G", Default(1, 1.0), 0.0, 1.0, 0.01, null, tooltip);
                yield return new EffectParameterDescriptor(ParameterName(p.Index, "b"), p.Name + " B", Default(2, 1.0), 0.0, 1.0, 0.01, null, tooltip);
                break;

            case Frei0rAbi.ParamPosition:
                yield return new EffectParameterDescriptor(ParameterName(p.Index, "x"), p.Name + " X", Default(0, 0.5), 0.0, 1.0, 0.01, null, tooltip);
                yield return new EffectParameterDescriptor(ParameterName(p.Index, "y"), p.Name + " Y", Default(1, 0.5), 0.0, 1.0, 0.01, null, tooltip);
                break;

            // STRING: no numeric model; the plugin keeps its default.
        }
    }

    /// <summary>Reduces a library file name to id-safe characters (lower-case letters, digits, dot, dash, underscore).</summary>
    internal static string Sanitize(string key)
    {
        Span<char> buffer = key.Length <= 128 ? stackalloc char[key.Length] : new char[key.Length];
        int n = 0;
        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                buffer[n++] = char.ToLowerInvariant(c);
        }
        return n > 0 ? new string(buffer[..n]) : "unnamed";
    }
}
