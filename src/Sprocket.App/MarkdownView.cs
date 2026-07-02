using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sprocket.App;

/// <summary>
/// A minimal markdown → Avalonia-controls renderer for the Help ▸ Third-Party Notices dialog (PLAN.md
/// step 36a). Deliberately hand-rolled on the native <see cref="SelectableTextBlock"/> inline model rather
/// than a markdown/HTML NuGet (Markdown.Avalonia, HtmlRenderer, a WebView): the input is one known,
/// checked-in document (THIRD-PARTY-NOTICES.md), so only the constructs it actually uses are supported —
/// <c>#</c>/<c>##</c> headings, paragraphs, <c>-</c> bullet lists, pipe tables (as <see cref="Grid"/>s,
/// which inlines alone cannot express), and the inline forms <c>**bold**</c>, <c>`code`</c>,
/// <c>[text](url)</c>, and <c>&lt;https://…&gt;</c> autolinks — and the pinned dependency set (CLAUDE.md)
/// stays untouched. Links open via the <see cref="TopLevel"/> launcher (the same pattern as the About
/// box's Open Logs Folder button); relative targets (e.g. <c>LICENSE</c>, the bundled font license)
/// resolve against a caller-supplied base directory.
/// </summary>
internal static class MarkdownView
{
    private const double BodyFontSize = 13;

    public static Control Build(string markdown, string linkBaseDir)
    {
        var stack = new StackPanel { Spacing = 8 };
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');

        var paragraph = new List<string>();
        var table = new List<string>();
        StackPanel? list = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            stack.Children.Add(BodyText(string.Join(" ", paragraph), linkBaseDir));
            paragraph.Clear();
        }

        void FlushList()
        {
            if (list is null) return;
            stack.Children.Add(list);
            list = null;
        }

        void FlushTable()
        {
            if (table.Count == 0) return;
            stack.Children.Add(BuildTable(table, linkBaseDir));
            table.Clear();
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();

            if (line.StartsWith('|'))
            {
                FlushParagraph();
                FlushList();
                table.Add(line);
                continue;
            }
            FlushTable();

            if (line.Length == 0)
            {
                FlushParagraph();
                FlushList();
            }
            else if (line.StartsWith("## "))
            {
                FlushParagraph();
                FlushList();
                stack.Children.Add(Heading(line[3..], 16, new Thickness(0, 10, 0, 0)));
            }
            else if (line.StartsWith("# "))
            {
                FlushParagraph();
                FlushList();
                stack.Children.Add(Heading(line[2..], 20, default));
            }
            else if (line.StartsWith("- "))
            {
                FlushParagraph();
                list ??= new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 0, 0) };
                Control item = BodyText("• " + line[2..], linkBaseDir);
                list.Children.Add(item);
            }
            else
            {
                paragraph.Add(line.Trim());
            }
        }
        FlushParagraph();
        FlushList();
        FlushTable();

        return stack;
    }

    private static TextBlock Heading(string text, double size, Thickness margin) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.TextBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin,
    };

    private static SelectableTextBlock BodyText(string markdown, string linkBaseDir)
    {
        var block = new SelectableTextBlock
        {
            FontSize = BodyFontSize,
            Foreground = Palette.TextBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        AddInlines(block.Inlines!, markdown, linkBaseDir, BodyFontSize);
        return block;
    }

    /// <summary>A pipe table as a <see cref="Grid"/>: first row is the header (bold, panel background),
    /// the <c>|---|</c> separator row is skipped, and the last column gets double weight since the notices
    /// tables end in a long free-text Notes column.</summary>
    private static Grid BuildTable(List<string> rows, string linkBaseDir)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        string[][] cells = [.. rows.ConvertAll(r => r.Trim().Trim('|').Split('|'))];
        int cols = cells[0].Length;
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(c == cols - 1 ? 2 : 1, GridUnitType.Star)));

        int gridRow = 0;
        for (int r = 0; r < cells.Length; r++)
        {
            if (r == 1 && Regex.IsMatch(rows[1], @"^[\s\-:|]+$")) continue; // the header/body separator
            bool header = r == 0;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols && c < cells[r].Length; c++)
            {
                var text = new SelectableTextBlock
                {
                    FontSize = BodyFontSize - 1,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = Palette.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                };
                AddInlines(text.Inlines!, cells[r][c].Trim(), linkBaseDir, BodyFontSize - 1);
                var cell = new Border
                {
                    Child = text,
                    Padding = new Thickness(7, 5),
                    Background = header ? Palette.PanelBgBrush : null,
                    BorderBrush = Palette.PanelBgBrush,
                    BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, 1),
                };
                cell.SetValue(Grid.RowProperty, gridRow);
                cell.SetValue(Grid.ColumnProperty, c);
                grid.Children.Add(cell);
            }
            gridRow++;
        }
        return grid;
    }

    // One pass over the text, emitting plain runs between the recognised inline forms.
    private static readonly Regex InlineToken = new(
        @"(?<bold>\*\*[^*]+\*\*)|(?<code>`[^`]+`)|(?<link>\[[^\]]+\]\([^)\s]+\))|(?<auto><https?://[^>\s]+>)",
        RegexOptions.Compiled);

    private static void AddInlines(InlineCollection inlines, string text, string linkBaseDir, double fontSize)
    {
        int pos = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["bold"].Success)
            {
                inlines.Add(new Run(m.Value[2..^2]) { FontWeight = FontWeight.SemiBold });
            }
            else if (m.Groups["code"].Success)
            {
                inlines.Add(new Run(m.Value[1..^1])
                {
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    Foreground = Palette.MutedTextBrush,
                });
            }
            else if (m.Groups["link"].Success)
            {
                int split = m.Value.IndexOf("](", StringComparison.Ordinal);
                // Link text may itself carry `code`/**bold** markers — show it plain.
                string label = m.Value[1..split].Replace("`", "").Replace("**", "");
                string url = m.Value[(split + 2)..^1];
                inlines.Add(Link(label, url, linkBaseDir, fontSize));
            }
            else // autolink <https://…>
            {
                string url = m.Value[1..^1];
                inlines.Add(Link(url, url, linkBaseDir, fontSize));
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]));
    }

    /// <summary>A clickable link inline: an underlined accent-colored TextBlock hosted in an
    /// <see cref="InlineUIContainer"/> (Avalonia has no Hyperlink inline), opened via the window's
    /// launcher on click.</summary>
    private static InlineUIContainer Link(string label, string url, string linkBaseDir, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            Foreground = Palette.AccentBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Bottom,
            // Baseline alignment puts the control's BOTTOM edge on the text baseline, which lifts the
            // glyphs by the descender space below them; pull back down by roughly that much.
            Margin = new Thickness(0, 0, 0, -Math.Round(fontSize * 0.28)),
        };
        tb.PointerPressed += async (_, _) =>
        {
            try
            {
                var uri = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url)
                    : new Uri(Path.GetFullPath(Path.Combine(linkBaseDir, url)));
                if (TopLevel.GetTopLevel(tb)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // Best-effort, like the About box's Open Logs Folder.
            }
        };
        return new InlineUIContainer(tb) { BaselineAlignment = BaselineAlignment.Baseline };
    }
}
