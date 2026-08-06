using Avalonia.Media;

namespace Sprocket.App;

/// <summary>
/// The single source of truth for Sprocket's text sizes and the one monospace stack (STYLE_GUIDE.md
/// "Typography"). The companion to <see cref="Palette"/>: colors were tokenized long before text was,
/// and typography drifted the way the palette had — ~167 hardcoded <c>FontSize</c> literals across ~14
/// files spanning nine distinct values (9, 10, 10.5, 11, 11.5, 12, 13, 14, 20), split by file/author
/// rather than by semantic role, plus two divergent hardcoded monospace stacks. The mixer tab was the
/// visible symptom: a 20px Integrated-LUFS readout beside controls that set no size at all and so
/// inherited Fluent's 14px default, both a step out of the app's 11–12px chrome.
/// <para>
/// Both the XAML shell (via <c>{x:Static}</c>) and the code-built views consume these. The Fluent
/// default is pinned to <see cref="Body"/> in <c>App.axaml</c>
/// (<c>ControlContentThemeFontSize</c>/<c>ToolTipContentThemeFontSize</c>), so an un-sized control or
/// TextBlock already renders at Body — only set a size to <em>depart</em> from Body.
/// </para>
/// </summary>
public static class Typography
{
    // Two "12-ish" tiers are intentional: Caption (11) is the density tier for docked panels; Body (12)
    // is the standard tier for dialogs/menus/buttons. Don't "unify" them — see STYLE_GUIDE.md.
    public static readonly double Micro = 10;    // meter captions, chips, densest annotations
    public static readonly double Caption = 11;  // panel labels & values, status bar
    public static readonly double Body = 12;     // dialog labels/inputs, menus, buttons, general UI
    public static readonly double Emphasis = 13; // dialog message text, transport/caption glyphs
    public static readonly double Title = 14;    // dialog titles, large numeric readouts (LUFS)

    // The one monospace stack (timecode, telemetry, dB/LUFS numerics, code spans).
    public static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");
}
