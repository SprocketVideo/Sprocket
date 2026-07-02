using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render;

/// <summary>
/// Draws the title-family generators (PLAN.md step 40): word-wrapped multi-line text with typography
/// (bundled <see cref="TitleFonts"/> family, bold/italic, alignment, tracking, leading), styling (fill,
/// stroke, drop shadow, padded background box), block positioning, a typewriter reveal, and the Roll/Crawl
/// scroll modes driven by the clip's local progress (<see cref="ResolvedGenerator.Progress"/> — the clip's
/// duration sets the speed). A step-19 title (none of the new parameters set) renders as before: a single
/// centred line over the full-frame background. All sizes are fractions of the frame height
/// (resolution-independent); everything is a pure function of the resolved generator, so preview and export
/// stay identical (ARCHITECTURE.md §5).
/// </summary>
public static class TitleRenderer
{
    /// <summary>One laid-out line: its text, the font it draws with, and its measured width.</summary>
    private readonly record struct Line(string Text, SKFont Font, float Width, float Height);

    /// <summary>Renders the title into <paramref name="canvas"/> (clears the full-frame background first).</summary>
    public static void Draw(SKCanvas canvas, ResolvedGenerator generator, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(generator);

        canvas.Clear(ParseColor(generator.GetString(GeneratorParamNames.BackgroundColor), SKColors.Transparent));

        string text = generator.GetString(GeneratorParamNames.Text);
        string text2 = generator.GetString(GeneratorParamNames.Text2);
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(text2))
            return;

        // ── Style ────────────────────────────────────────────────────────────────────────────────────
        string family = generator.GetString(GeneratorParamNames.FontFamily, TitleFonts.DefaultFamily);
        bool bold = IsTrue(generator, GeneratorParamNames.Bold);
        bool italic = IsTrue(generator, GeneratorParamNames.Italic);
        SKTypeface typeface = TitleFonts.Get(family, bold, italic);

        float size = (float)generator.Get(GeneratorParamNames.FontSize, 0.12) * height;
        float size2 = (float)generator.Get(GeneratorParamNames.FontSize2, 0.6 * generator.Get(GeneratorParamNames.FontSize, 0.12)) * height;
        float leading = (float)generator.Get(GeneratorParamNames.Leading, 1.2);
        float tracking = (float)generator.Get(GeneratorParamNames.Tracking, 0.0);
        string alignment = generator.GetString(GeneratorParamNames.Alignment, "center");

        string scrollMode = generator.GetString(GeneratorParamNames.ScrollMode, TitleScrollModes.None);
        bool rolls = scrollMode == TitleScrollModes.Roll;
        bool crawls = scrollMode == TitleScrollModes.Crawl;

        using var font = new SKFont(typeface, Math.Max(1f, size));
        using var font2 = new SKFont(typeface, Math.Max(1f, size2));

        // ── Layout: wrap into lines (a crawl stays one line), then apply the typewriter reveal ─────────
        float wrapWidth = crawls ? float.MaxValue : width * 0.9f;
        var lines = new List<Line>();
        LayOut(crawls ? Flatten(text) : text, font, leading, tracking, wrapWidth, lines);
        if (!string.IsNullOrEmpty(text2))
            LayOut(crawls ? Flatten(text2) : text2, font2, leading, tracking, wrapWidth, lines);

        double reveal = generator.Get(GeneratorParamNames.RevealFraction, 1.0);
        if (reveal < 1.0)
            ApplyReveal(lines, reveal, tracking);
        if (lines.Count == 0)
            return;

        float blockWidth = 0, blockHeight = 0;
        foreach (Line line in lines)
        {
            blockWidth = Math.Max(blockWidth, line.Width);
            blockHeight += line.Height;
        }

        // ── Position + scroll (PLAN.md step 40): the block centre rests at (positionX, positionY); a roll/
        // crawl interpolates from its start to its end position by the eased clip-local progress, so the
        // clip's duration sets the speed and the motion survives trim/move (no absolute-time keyframes). ──
        float restLeft = (float)generator.Get(GeneratorParamNames.PositionX, 0.5) * width - blockWidth / 2f;
        float restTop = (float)generator.Get(GeneratorParamNames.PositionY, 0.5) * height - blockHeight / 2f;
        float blockLeft = restLeft, blockTop = restTop;

        if (rolls || crawls)
        {
            double eased = TitleScroll.Eased(
                generator.Progress,
                IsTrue(generator, GeneratorParamNames.ScrollEaseIn),
                IsTrue(generator, GeneratorParamNames.ScrollEaseOut));
            bool offscreen = generator.GetString(GeneratorParamNames.ScrollOffscreen, "true") != "false";
            if (rolls)
            {
                float startTop = offscreen ? height : restTop;
                float endTop = offscreen ? -blockHeight : restTop;
                blockTop = Lerp(startTop, endTop, (float)eased);
            }
            else
            {
                float startLeft = offscreen ? width : restLeft;
                float endLeft = offscreen ? -blockWidth : restLeft;
                blockLeft = Lerp(startLeft, endLeft, (float)eased);
            }
        }

        // ── Background box (moves with the block — the lower third's bar) ──────────────────────────────
        SKColor boxColor = ParseColor(generator.GetString(GeneratorParamNames.BoxColor), SKColors.Transparent);
        if (boxColor.Alpha > 0)
        {
            float pad = (float)generator.Get(GeneratorParamNames.BoxPadding, 0.02) * height;
            using var boxPaint = new SKPaint { Color = boxColor, IsAntialias = true };
            canvas.DrawRect(
                SKRect.Create(blockLeft - pad, blockTop - pad, blockWidth + 2 * pad, blockHeight + 2 * pad),
                boxPaint);
        }

        // ── Text passes: shadow → stroke → fill ────────────────────────────────────────────────────────
        SKColor fill = ParseColor(generator.GetString(GeneratorParamNames.Color), SKColors.White);
        SKColor shadowColor = ParseColor(generator.GetString(GeneratorParamNames.ShadowColor), SKColors.Transparent);
        float strokeWidth = (float)generator.Get(GeneratorParamNames.StrokeWidth, 0.0) * height;

        if (shadowColor.Alpha > 0)
        {
            float dx = (float)generator.Get(GeneratorParamNames.ShadowOffsetX, 0.004) * height;
            float dy = (float)generator.Get(GeneratorParamNames.ShadowOffsetY, 0.004) * height;
            float blur = (float)generator.Get(GeneratorParamNames.ShadowBlur, 0.004) * height;
            using var shadow = new SKPaint { Color = shadowColor, IsAntialias = true };
            if (blur > 0)
                shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
            DrawLines(canvas, lines, blockLeft + dx, blockTop + dy, blockWidth, alignment, tracking, shadow);
            shadow.MaskFilter?.Dispose();
        }

        if (strokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Color = ParseColor(generator.GetString(GeneratorParamNames.StrokeColor), SKColors.Black),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
            };
            DrawLines(canvas, lines, blockLeft, blockTop, blockWidth, alignment, tracking, stroke);
        }

        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        DrawLines(canvas, lines, blockLeft, blockTop, blockWidth, alignment, tracking, paint);
    }

    // ── Layout ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A crawl is a single line: hard newlines flatten to spaces.</summary>
    private static string Flatten(string text) => text.Replace('\n', ' ');

    /// <summary>
    /// Splits <paramref name="text"/> on hard newlines, word-wraps each paragraph to
    /// <paramref name="wrapWidth"/> (measured with <paramref name="tracking"/>), and appends the resulting
    /// lines. An over-long word gets its own line rather than being broken mid-word.
    /// </summary>
    private static void LayOut(string text, SKFont font, float leading, float tracking, float wrapWidth, List<Line> lines)
    {
        if (string.IsNullOrEmpty(text))
            return;

        float lineHeight = font.Size * leading;
        float trackingPx = tracking * font.Size;
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(new Line("", font, 0, lineHeight)); // an empty paragraph keeps its vertical space
                continue;
            }

            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && MeasureTracked(font, candidate, trackingPx) > wrapWidth)
                {
                    lines.Add(new Line(current, font, MeasureTracked(font, current, trackingPx), lineHeight));
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            lines.Add(new Line(current, font, MeasureTracked(font, current, trackingPx), lineHeight));
        }
    }

    /// <summary>
    /// The typewriter reveal (PLAN.md step 40): keeps only the first <paramref name="reveal"/> fraction of
    /// the block's characters (line breaks preserved, so text appears in reading order while the block's
    /// size and position stay fixed — revealed lines keep their slot).
    /// </summary>
    private static void ApplyReveal(List<Line> lines, double reveal, float tracking)
    {
        int total = 0;
        foreach (Line line in lines)
            total += line.Text.Length;
        int keep = (int)Math.Round(Math.Clamp(reveal, 0.0, 1.0) * total);

        for (int i = 0; i < lines.Count; i++)
        {
            Line line = lines[i];
            int take = Math.Min(line.Text.Length, Math.Max(0, keep));
            keep -= line.Text.Length;
            if (take == line.Text.Length)
                continue;
            // Truncated line: keep the measured full width (the block must not re-flow as it reveals).
            lines[i] = new Line(line.Text[..take], line.Font, line.Width, line.Height);
        }
    }

    private static void DrawLines(
        SKCanvas canvas, List<Line> lines, float blockLeft, float blockTop, float blockWidth,
        string alignment, float tracking, SKPaint paint)
    {
        float y = blockTop;
        foreach (Line line in lines)
        {
            if (line.Text.Length > 0)
            {
                float x = alignment switch
                {
                    "left" => blockLeft,
                    "right" => blockLeft + blockWidth - line.Width,
                    _ => blockLeft + (blockWidth - line.Width) / 2f,
                };
                // Baseline: top of the line slot plus the ascent (Ascent is negative in Skia).
                float baseline = y - line.Font.Metrics.Ascent + (line.Height - line.Font.Spacing) / 2f;
                float trackingPx = tracking * line.Font.Size;
                if (trackingPx == 0)
                {
                    canvas.DrawText(line.Text, x, baseline, SKTextAlign.Left, line.Font, paint);
                }
                else
                {
                    // Manual per-glyph advance: Skia has no letter-spacing on SKFont; titles are short
                    // strings so the per-character draw is cheap and fully deterministic.
                    float cx = x;
                    foreach (char c in line.Text)
                    {
                        string s = c.ToString();
                        canvas.DrawText(s, cx, baseline, SKTextAlign.Left, line.Font, paint);
                        cx += line.Font.MeasureText(s) + trackingPx;
                    }
                }
            }
            y += line.Height;
        }
    }

    /// <summary>Measures a line's width including per-character tracking.</summary>
    private static float MeasureTracked(SKFont font, string text, float trackingPx)
    {
        if (text.Length == 0)
            return 0;
        if (trackingPx == 0)
            return font.MeasureText(text);
        float width = 0;
        foreach (char c in text)
            width += font.MeasureText(c.ToString());
        return width + trackingPx * (text.Length - 1);
    }

    private static bool IsTrue(ResolvedGenerator generator, string name) =>
        generator.GetString(name) == "true";

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    /// <summary>Parses a <c>#AARRGGBB</c>/<c>#RRGGBB</c> colour string, falling back to <paramref name="fallback"/>.</summary>
    internal static SKColor ParseColor(string value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out SKColor color) ? color : fallback;
}
