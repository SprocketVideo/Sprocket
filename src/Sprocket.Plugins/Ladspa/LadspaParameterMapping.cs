using Sprocket.Core.Model;

namespace Sprocket.Plugins.Ladspa;

/// <summary>
/// Maps a LADSPA plugin's ports onto Sprocket's type-driven effect model (PLAN.md step 59): each control-input
/// port becomes a typed <see cref="EffectParameterDescriptor"/> (min/max/default/kind derived from the LADSPA
/// range hints), and the whole plugin becomes an <see cref="EffectDescriptor"/> in
/// <see cref="EffectCategory.Audio"/> so the render graph routes it to the mixer chain and the Inspector builds
/// its controls with no bespoke UI (ARCHITECTURE.md §4).
/// </summary>
/// <remarks>
/// Pure — it touches no native memory, so the mapping (the part with all the hint arithmetic) is unit-tested
/// directly against hand-built <see cref="LadspaPluginInfo"/>s. Parameter keys are <c>port&lt;index&gt;</c>
/// (the LADSPA port index is stable per plugin version), so a saved project re-binds its values even though
/// LADSPA gives no per-port symbol. Range hints flagged <c>SAMPLE_RATE</c> are resolved against a nominal rate
/// (<see cref="NominalSampleRate"/>) for the slider bounds only — the value the user sets is passed to the
/// plugin verbatim, so a project at a different rate still drives the plugin correctly.
/// </remarks>
internal static class LadspaParameterMapping
{
    /// <summary>The nominal sample rate used to resolve <c>SAMPLE_RATE</c>-scaled hint bounds for display.</summary>
    public const double NominalSampleRate = 48000.0;

    /// <summary>The effect-type-id prefix every LADSPA-hosted effect shares.</summary>
    public const string IdPrefix = "plugin.ladspa.";

    /// <summary>The stable parameter key for the control port at <paramref name="portIndex"/>.</summary>
    public static string ParameterName(int portIndex) => "port" + portIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The stable effect type id for a plugin: <c>plugin.ladspa.&lt;uniqueId&gt;</c> when the plugin carries a
    /// LADSPA-registry unique id (globally unique and independent of where the library file lives), else
    /// <c>plugin.ladspa.&lt;label&gt;</c> for an unregistered/dev plugin.
    /// </summary>
    public static string EffectId(LadspaPluginInfo info)
    {
        if (info.UniqueId != 0)
            return IdPrefix + info.UniqueId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string label = Sanitize(info.Label);
        return IdPrefix + (label.Length > 0 ? label : "unnamed");
    }

    /// <summary>Builds the catalog descriptor for a LADSPA plugin (one control-input port → one parameter).</summary>
    public static EffectDescriptor ToEffectDescriptor(LadspaPluginInfo info)
    {
        var parameters = new List<EffectParameterDescriptor>();
        foreach (LadspaPortInfo port in info.ControlInputs)
            parameters.Add(ToParameter(port));

        string displayName = FirstNonEmpty(info.Name, info.Label, "LADSPA Plugin");
        string description = info.Maker.Length > 0
            ? $"LADSPA plugin by {info.Maker}."
            : "LADSPA plugin.";

        return new EffectDescriptor(EffectId(info), displayName, EffectCategory.Audio, description, parameters);
    }

    /// <summary>Maps one control-input port's range hints to a parameter descriptor.</summary>
    public static EffectParameterDescriptor ToParameter(LadspaPortInfo port)
    {
        int hint = port.HintDescriptor;
        bool toggled = (hint & LadspaAbi.HintToggled) != 0;
        bool integer = (hint & LadspaAbi.HintInteger) != 0;
        bool logarithmic = (hint & LadspaAbi.HintLogarithmic) != 0;

        double scale = (hint & LadspaAbi.HintSampleRate) != 0 ? NominalSampleRate : 1.0;
        bool boundedBelow = (hint & LadspaAbi.HintBoundedBelow) != 0;
        bool boundedAbove = (hint & LadspaAbi.HintBoundedAbove) != 0;
        double lower = port.LowerBound * scale;
        double upper = port.UpperBound * scale;

        string name = LadspaParameterMapping.ParameterName(port.Index);
        string display = port.Name.Length > 0 ? port.Name : name;

        if (toggled)
        {
            double toggleDefault = (hint & LadspaAbi.HintDefaultMask) == LadspaAbi.HintDefault1 ? 1.0 : 0.0;
            return new EffectParameterDescriptor(name, display, toggleDefault, 0.0, 1.0, 1.0,
                Kind: ParameterKind.Toggle);
        }

        (double min, double max) = ResolveRange(boundedBelow, boundedAbove, lower, upper);
        double def = ResolveDefault(hint, boundedBelow, boundedAbove, min, max, logarithmic);
        def = Math.Clamp(def, min, max);
        if (integer)
            def = Math.Round(def);

        double step = integer ? 1.0 : Math.Max((max - min) / 100.0, 1e-4);
        return new EffectParameterDescriptor(name, display, def, min, max, step,
            Kind: integer ? ParameterKind.Integer : ParameterKind.Continuous);
    }

    /// <summary>Resolves the slider range, inventing a sensible finite range when a bound is missing (LADSPA
    /// permits unbounded control ports, but the Inspector needs a range to draw a slider).</summary>
    private static (double Min, double Max) ResolveRange(bool boundedBelow, bool boundedAbove, double lower, double upper)
    {
        double min = boundedBelow ? lower : (boundedAbove ? Math.Min(0.0, upper - 1.0) : 0.0);
        double max = boundedAbove ? upper : (boundedBelow ? lower + Math.Max(Math.Abs(lower), 1.0) : 1.0);
        if (max <= min)
            max = min + 1.0;
        return (min, max);
    }

    /// <summary>Resolves the default value from the LADSPA default hint bits, honouring the logarithmic hint for
    /// the LOW/MIDDLE/HIGH interpolations (as the LADSPA SDK host does).</summary>
    private static double ResolveDefault(int hint, bool boundedBelow, bool boundedAbove, double min, double max, bool logarithmic)
    {
        bool canLog = logarithmic && min > 0.0 && max > 0.0;
        double LogLerp(double t) => Math.Exp(Math.Log(min) * (1.0 - t) + Math.Log(max) * t);
        double Lerp(double t) => canLog ? LogLerp(t) : min * (1.0 - t) + max * t;

        return (hint & LadspaAbi.HintDefaultMask) switch
        {
            LadspaAbi.HintDefaultMinimum => min,
            LadspaAbi.HintDefaultLow => Lerp(0.25),
            LadspaAbi.HintDefaultMiddle => Lerp(0.5),
            LadspaAbi.HintDefaultHigh => Lerp(0.75),
            LadspaAbi.HintDefaultMaximum => max,
            LadspaAbi.HintDefault0 => 0.0,
            LadspaAbi.HintDefault1 => 1.0,
            LadspaAbi.HintDefault100 => 100.0,
            LadspaAbi.HintDefault440 => 440.0,
            // DEFAULT_NONE (or an unknown mask): prefer the lower bound, then the upper, then zero.
            _ => boundedBelow ? min : (boundedAbove ? max : 0.0),
        };
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (string c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c;
        return "";
    }

    /// <summary>Reduces a label to id-safe characters (letters, digits, dot, dash, underscore).</summary>
    private static string Sanitize(string label)
    {
        Span<char> buffer = label.Length <= 128 ? stackalloc char[label.Length] : new char[label.Length];
        int n = 0;
        foreach (char c in label)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..n]);
    }
}
