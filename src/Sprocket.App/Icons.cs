using Avalonia.Media;

namespace Sprocket.App;

/// <summary>
/// Vector icon geometry replacing the Unicode glyph characters (▶, ✕, 👁, ◆, …) previously used as button
/// content: those glyphs are resolved by whichever font the OS substitutes for a given codepoint, so the same
/// character rendered a different shape on Windows vs. macOS. Path data here is transcribed from Feather
/// (MIT-licensed, https://github.com/feathericons/feather) on its native 24x24 grid at ~2px stroke weight;
/// consumers scale via <c>Path.Width</c>/<c>Height</c> with <c>Stretch="Uniform"</c> so the rendered shape is
/// identical on every platform.
/// <para>
/// Feather has no diamond primitive — <see cref="Diamond"/> is a hand-drawn rounded square in Feather's stroke
/// style, pre-rotated 45° in the path data itself so no <c>RenderTransform</c> is needed at any call site.
/// Feather also ships no literal "restore window" icon — <see cref="Restore"/> uses Feather's "copy" glyph,
/// the closest semantic match the set has.
/// </para>
/// </summary>
/// <summary>
/// The reference (larger-axis) dimension for each icon tier. An icon's other axis is still derived from its own
/// aspect ratio (see the per-icon comments in <see cref="Icons"/>) — <c>Stretch="Uniform"</c> against a box sized
/// to anything other than the icon's exact aspect ratio leaves slack that can render off-center (see StepBack/
/// StepForward in MainWindow.axaml), so only one axis is ever a shared constant.
/// </summary>
public static class IconSizes
{
    /// Window caption glyphs (minimize/maximize/close) — the title bar's own compact 34px height caps these.
    public const double Chrome = 8;

    /// Ultra-dense per-row glyphs sitting inside a 12px-text strip (the Inspector's effect title bars), where
    /// even Compact reads oversized against the label.
    public const double Dense = 10;

    /// Dense inline controls: per-item toggles and small remove buttons (Inspector effect rows, marker list rows,
    /// the Timeline's copy-drag "+" badge).
    public const double Compact = 11;

    /// Default control icon: toolbar, transport, and Inspector section glyphs.
    public const double Default = 13;

    /// Multi-element composite icons (e.g. chevron + diamond) that need extra width to read clearly at a glance.
    public const double Composite = 15;

    /// Large content-placeholder graphics (e.g. the media bin's poster-fallback icon before a thumbnail loads) —
    /// not a control icon, so it sits outside the control-icon scale above.
    public const double Placeholder = 20;
}

public static class Icons
{
    // Window chrome. Minimize is a filled bar rather than a stroked line: a perfectly horizontal line has a
    // zero-height bounding box, and Stretch="Uniform" against a zero-height box collapses the render to a
    // near-invisible dot instead of a line.
    public static readonly Geometry Minimize = Geometry.Parse("M5,11 L19,11 L19,13 L5,13 Z");
    public static readonly Geometry Maximize = Geometry.Parse("M5,3 L19,3 A2,2 0 0 1 21,5 L21,19 A2,2 0 0 1 19,21 L5,21 A2,2 0 0 1 3,19 L3,5 A2,2 0 0 1 5,3 Z");
    public static readonly Geometry Restore = Geometry.Parse("M11,9 L20,9 A2,2 0 0 1 22,11 L22,20 A2,2 0 0 1 20,22 L11,22 A2,2 0 0 1 9,20 L9,11 A2,2 0 0 1 11,9 Z M5,15 H4 a2,2 0 0 1 -2,-2 V4 a2,2 0 0 1 2,-2 h9 a2,2 0 0 1 2,2 v1");
    public static readonly Geometry Close = Geometry.Parse("M18,6 L6,18 M6,6 L18,18");

    // Transport.
    public static readonly Geometry SkipBack = Geometry.Parse("M19,20 L9,12 L19,4 Z M5,19 L5,5");
    public static readonly Geometry Rewind = Geometry.Parse("M11,19 L2,12 L11,5 Z M22,19 L13,12 L22,5 Z");
    public static readonly Geometry Play = Geometry.Parse("M5,3 L19,12 L5,21 Z");
    public static readonly Geometry Pause = Geometry.Parse("M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z");
    public static readonly Geometry FastForward = Geometry.Parse("M13,19 L22,12 L13,5 Z M2,19 L11,12 L2,5 Z");
    public static readonly Geometry SkipForward = Geometry.Parse("M5,4 L15,12 L5,20 Z M19,5 L19,19");

    // Keyframe state (used for the Inspector's per-parameter toggle).
    public static readonly Geometry Diamond = Geometry.Parse("M13.06,4.58 L19.42,10.94 A1.5,1.5 0 0 1 19.42,13.06 L13.06,19.42 A1.5,1.5 0 0 1 10.94,19.42 L4.58,13.06 A1.5,1.5 0 0 1 4.58,10.94 L10.94,4.58 A1.5,1.5 0 0 1 13.06,4.58 Z");

    // Prev/next-keyframe transport icons: chevron + diamond baked into ONE geometry (rather than composing two
    // separately-stretched Paths in a StackPanel) so the gap between them is an authored constant, not the
    // difference of two independent Stretch="Uniform" gutters — which rendered a visibly uneven gap between the
    // two directions despite both Paths using the same size numbers. Diamond here is Diamond above, scaled 0.606x
    // and translated (translate/scale only, never mirrored, since mirroring would flip the arc sweep-flags).
    public static readonly Geometry PrevKeyframe = Geometry.Parse(
        "M15,18 L9,12 L15,6 M22.14,7.5 L26,11.36 A0.91,0.91 0 0 1 26,12.64 L22.14,16.5 A0.91,0.91 0 0 1 20.86,16.5 " +
        "L17,12.64 A0.91,0.91 0 0 1 17,11.36 L20.86,7.5 A0.91,0.91 0 0 1 22.14,7.5 Z");
    public static readonly Geometry NextKeyframe = Geometry.Parse(
        "M14.14,7.5 L18,11.36 A0.91,0.91 0 0 1 18,12.64 L14.14,16.5 A0.91,0.91 0 0 1 12.86,16.5 " +
        "L9,12.64 A0.91,0.91 0 0 1 9,11.36 L12.86,7.5 A0.91,0.91 0 0 1 14.14,7.5 Z M20,18 L26,12 L20,6");

    // Inspector / effects. A single Eye glyph is used for both on/off states (color-coded, as the Unicode
    // "👁" it replaces was) rather than swapping to a crossed-out variant.
    public static readonly Geometry Activity = Geometry.Parse("M22,12 L18,12 L15,21 L9,3 L6,12 L2,12");
    public static readonly Geometry Eye = Geometry.Parse("M1,12 s4,-8 11,-8 11,8 11,8 -4,8 -11,8 -11,-8 -11,-8 Z M9,12 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0");

    // Media browser tile fallback.
    public static readonly Geometry Music = Geometry.Parse("M9,18 V5 l12,-2 v13 M3,18 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M15,16 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0");
    public static readonly Geometry Film = Geometry.Parse("M4.18,2 L19.82,2 A2.18,2.18 0 0 1 22,4.18 L22,19.82 A2.18,2.18 0 0 1 19.82,22 L4.18,22 A2.18,2.18 0 0 1 2,19.82 L2,4.18 A2.18,2.18 0 0 1 4.18,2 Z M7,2 L7,22 M17,2 L17,22 M2,12 L22,12 M2,7 L7,7 M2,17 L7,17 M17,17 L22,17 M17,7 L22,7");

    // Timeline canvas drag hint.
    public static readonly Geometry Plus = Geometry.Parse("M12,5 L12,19 M5,12 L19,12");

    // Inspector pane-header expand/collapse-all (Feather chevrons-down / chevrons-up).
    public static readonly Geometry ChevronsDown = Geometry.Parse("M7,13 L12,18 L17,13 M7,6 L12,11 L17,6");
    public static readonly Geometry ChevronsUp = Geometry.Parse("M17,11 L12,6 L7,11 M17,18 L12,13 L7,18");
}
