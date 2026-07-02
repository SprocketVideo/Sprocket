namespace Sprocket.Core.Model;

/// <summary>
/// Well-known built-in generator type ids (PLAN.md step 19). A generator produces a video frame procedurally
/// instead of decoding source media — the render graph drives it through the generator seam exactly as it drives
/// a decoded layer (ARCHITECTURE.md §5). Plugins would use their own namespaced ids, like effects (§13).
/// </summary>
public static class GeneratorTypeIds
{
    /// <summary>A solid colour matte filling the frame. Parameter: <see cref="GeneratorParamNames.Color"/>.</summary>
    public const string SolidColor = "builtin.gen.solidcolor";

    /// <summary>
    /// A text/title over a (optionally transparent) background. Parameters:
    /// <see cref="GeneratorParamNames.Text"/>, <see cref="GeneratorParamNames.Color"/>,
    /// <see cref="GeneratorParamNames.BackgroundColor"/>, <see cref="GeneratorParamNames.FontSize"/>, and the
    /// step-40 typography/layout/scroll parameters (see <see cref="GeneratorParamNames"/>).
    /// </summary>
    public const string Title = "builtin.gen.title";

    /// <summary>A two-field name+role title with a background bar, anchored lower-left (PLAN.md step 40).
    /// Same render path as <see cref="Title"/>; a distinct id so the catalog/browser list it separately.</summary>
    public const string LowerThird = "builtin.gen.lowerthird";

    /// <summary>Rolling credits (PLAN.md step 40): a multi-line title scrolling bottom→top over the clip's
    /// duration (<see cref="GeneratorParamNames.ScrollMode"/> = <c>roll</c>). Longer clip ⇒ slower roll.</summary>
    public const string Roll = "builtin.gen.roll";

    /// <summary>A single-line ticker crawling right→left over the clip's duration
    /// (<see cref="GeneratorParamNames.ScrollMode"/> = <c>crawl</c>).</summary>
    public const string Crawl = "builtin.gen.crawl";

    /// <summary>Whether <paramref name="generatorTypeId"/> is one of the title-family generators — the ids that
    /// share the rich-text render path and the Inspector's TEXT section (PLAN.md step 40).</summary>
    public static bool IsTitle(string generatorTypeId) =>
        generatorTypeId is Title or LowerThird or Roll or Crawl;
}

/// <summary>Well-known parameter names used by the built-in generators. Colours are <c>#AARRGGBB</c> hex strings.</summary>
public static class GeneratorParamNames
{
    /// <summary>Foreground colour as <c>#AARRGGBB</c> hex (matte fill / text colour). A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string Color = "color";

    /// <summary>The title text. A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string Text = "text";

    /// <summary>Background colour behind the title as <c>#AARRGGBB</c> hex (use alpha 0 for transparent). A <see cref="GeneratorSpec.Strings"/> entry.</summary>
    public const string BackgroundColor = "backgroundColor";

    /// <summary>Title font size as a fraction of the frame height (e.g. 0.12). A numeric <see cref="GeneratorSpec.Parameters"/> entry.</summary>
    public const string FontSize = "fontSize";

    // ── Typography & styling (PLAN.md step 40). Sizes are fractions of the frame height (resolution-independent);
    // colours are #AARRGGBB hex strings. String attributes live in GeneratorSpec.Strings; numeric ones are
    // AnimatableValue Parameters so they keyframe (step 16d). Absent = the step-19 default (a step-19 title
    // renders unchanged and serializes byte-identically).

    /// <summary>Bundled font family name (e.g. <c>Inter</c>). Absent = the default bundled family. A string entry.</summary>
    public const string FontFamily = "fontFamily";

    /// <summary>Bold weight: <c>"true"</c>/absent. A string entry.</summary>
    public const string Bold = "bold";

    /// <summary>Italic style: <c>"true"</c>/absent. A string entry.</summary>
    public const string Italic = "italic";

    /// <summary>Paragraph alignment: <c>left</c> / <c>center</c> (default) / <c>right</c>. A string entry.</summary>
    public const string Alignment = "align";

    /// <summary>Outline colour as <c>#AARRGGBB</c> hex. Drawn when <see cref="StrokeWidth"/> &gt; 0. A string entry.</summary>
    public const string StrokeColor = "strokeColor";

    /// <summary>Outline width as a fraction of the frame height (e.g. 0.004). 0/absent = no stroke. Numeric, animatable.</summary>
    public const string StrokeWidth = "strokeWidth";

    /// <summary>Drop-shadow colour as <c>#AARRGGBB</c> hex (alpha 0/absent = no shadow). A string entry.</summary>
    public const string ShadowColor = "shadowColor";

    /// <summary>Drop-shadow X offset as a fraction of the frame height. Numeric, animatable.</summary>
    public const string ShadowOffsetX = "shadowOffsetX";

    /// <summary>Drop-shadow Y offset as a fraction of the frame height. Numeric, animatable.</summary>
    public const string ShadowOffsetY = "shadowOffsetY";

    /// <summary>Drop-shadow blur sigma as a fraction of the frame height. Numeric, animatable.</summary>
    public const string ShadowBlur = "shadowBlur";

    /// <summary>Background-box colour behind the text block as <c>#AARRGGBB</c> hex (alpha 0/absent = no box).
    /// The padded-box form of the full-frame <see cref="BackgroundColor"/>. A string entry.</summary>
    public const string BoxColor = "boxColor";

    /// <summary>Background-box padding around the text block as a fraction of the frame height. Numeric, animatable.</summary>
    public const string BoxPadding = "boxPadding";

    /// <summary>Letter spacing (tracking) as a fraction of the font size (em). 0/absent = normal. Numeric, animatable.</summary>
    public const string Tracking = "tracking";

    /// <summary>Line spacing (leading) as a multiple of the font size. Absent = 1.2. Numeric, animatable.</summary>
    public const string Leading = "leading";

    /// <summary>Text-block centre X as a fraction of the frame width. Absent = 0.5 (centred). Numeric, animatable.</summary>
    public const string PositionX = "positionX";

    /// <summary>Text-block centre Y as a fraction of the frame height. Absent = 0.5 (centred). Numeric, animatable.</summary>
    public const string PositionY = "positionY";

    /// <summary>Secondary text line (the lower third's role/subtitle field), drawn under <see cref="Text"/> at
    /// <see cref="FontSize2"/>. A string entry.</summary>
    public const string Text2 = "text2";

    /// <summary>Secondary text font size as a fraction of the frame height. Numeric, animatable.</summary>
    public const string FontSize2 = "fontSize2";

    // ── Scroll (Roll / Crawl — PLAN.md step 40). Scrolling is a property of the title driven by the clip's
    // local progress (duration sets the speed — the Resolve/FCP model), not hand-keyframed Transform moves.

    /// <summary>Scroll mode: <c>none</c> (default) / <c>roll</c> (bottom→top) / <c>crawl</c> (right→left). A string entry.</summary>
    public const string ScrollMode = "scrollMode";

    /// <summary>Ease the scroll in from rest: <c>"true"</c>/absent. A string entry.</summary>
    public const string ScrollEaseIn = "scrollEaseIn";

    /// <summary>Ease the scroll out to rest: <c>"true"</c>/absent. A string entry.</summary>
    public const string ScrollEaseOut = "scrollEaseOut";

    /// <summary>Start/end fully off-screen: <c>"true"</c> (default when absent) / <c>"false"</c> — when false the
    /// scroll starts/ends at the block's resting position (the Premiere start/end off-screen options). A string entry.</summary>
    public const string ScrollOffscreen = "scrollOffscreen";

    /// <summary>Typewriter reveal: the fraction of characters drawn, 0–1. Absent = 1 (all). Keyframe 0→1 for the
    /// typewriter entrance (PLAN.md step 40). Numeric, animatable.</summary>
    public const string RevealFraction = "reveal";
}

/// <summary>String values used by <see cref="GeneratorParamNames.ScrollMode"/>.</summary>
public static class TitleScrollModes
{
    /// <summary>No scrolling (the default).</summary>
    public const string None = "none";

    /// <summary>Rolling credits: the text block scrolls bottom→top over the clip's duration.</summary>
    public const string Roll = "roll";

    /// <summary>Ticker crawl: a single line scrolls right→left over the clip's duration.</summary>
    public const string Crawl = "crawl";
}

/// <summary>
/// Pure scroll math for rolling/crawling titles (PLAN.md step 40). The offset is a function of the clip's
/// <b>local progress</b> (0 at the clip's start, 1 at its end) so a roll is deterministic in (project, t)
/// (ARCHITECTURE.md §5), survives trim/move, and the clip's duration sets the speed — longer clip ⇒ slower.
/// The Render layer maps the eased progress onto pixels (start → end positions).
/// </summary>
public static class TitleScroll
{
    /// <summary>
    /// Applies optional ease-in/out to a linear progress. Both: smoothstep (3p²−2p³); ease-in only: p²;
    /// ease-out only: 1−(1−p)². Input and output are clamped to [0, 1].
    /// </summary>
    public static double Eased(double progress, bool easeIn, bool easeOut)
    {
        double p = Math.Clamp(progress, 0.0, 1.0);
        if (easeIn && easeOut)
            return p * p * (3.0 - 2.0 * p);
        if (easeIn)
            return p * p;
        if (easeOut)
            return 1.0 - (1.0 - p) * (1.0 - p);
        return p;
    }
}

/// <summary>
/// The procedural source of a <see cref="ClipKind.Generator"/> clip (PLAN.md step 19): a generator type id plus
/// its parameters. String parameters (text, colours) live in <see cref="Strings"/>; numeric, animatable ones
/// (e.g. font size) live in <see cref="Parameters"/> as <see cref="AnimatableValue"/>s — the same mechanism
/// effects use, so a generator parameter can keyframe. Plain data with no native handles, so it serializes with
/// the project and is evaluated by the render graph at the frame's time; the Render layer owns the actual drawing.
/// </summary>
public sealed class GeneratorSpec
{
    /// <summary>Creates a generator spec of the given type.</summary>
    public GeneratorSpec(string generatorTypeId)
    {
        if (string.IsNullOrWhiteSpace(generatorTypeId))
            throw new ArgumentException("Generator type id is required.", nameof(generatorTypeId));
        GeneratorTypeId = generatorTypeId;
    }

    /// <summary>The generator type id, e.g. <see cref="GeneratorTypeIds.Title"/>.</summary>
    public string GeneratorTypeId { get; }

    /// <summary>String parameters by name (text, colour hex). See <see cref="GeneratorParamNames"/>.</summary>
    public Dictionary<string, string> Strings { get; } = new();

    /// <summary>Numeric, animatable parameters by name, each an <see cref="AnimatableValue"/>.</summary>
    public Dictionary<string, AnimatableValue> Parameters { get; } = new();

    /// <summary>Sets a string parameter (fluent).</summary>
    public GeneratorSpec SetString(string name, string value)
    {
        Strings[name] = value;
        return this;
    }

    /// <summary>Sets a numeric parameter to a constant (fluent).</summary>
    public GeneratorSpec Set(string name, double value)
    {
        Parameters[name] = AnimatableValue.Constant(value);
        return this;
    }

    /// <summary>Sets a numeric parameter to an animatable value (fluent).</summary>
    public GeneratorSpec Set(string name, AnimatableValue value)
    {
        Parameters[name] = value;
        return this;
    }

    /// <summary>Gets a string parameter, or <paramref name="fallback"/> if it is not set.</summary>
    public string GetString(string name, string fallback = "") =>
        Strings.TryGetValue(name, out string? value) ? value : fallback;

    /// <summary>
    /// A copy with the same type and parameters. <see cref="AnimatableValue"/> is immutable so numeric entries are
    /// shared by reference; the maps are fresh. Used when a blade split copies a generator clip (PLAN.md step 13).
    /// </summary>
    public GeneratorSpec Clone()
    {
        var copy = new GeneratorSpec(GeneratorTypeId);
        foreach ((string name, string value) in Strings)
            copy.Strings[name] = value;
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value;
        return copy;
    }
}
