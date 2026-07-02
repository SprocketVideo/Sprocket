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
public static class Icons
{
    // Window chrome.
    public static readonly Geometry Minimize = Geometry.Parse("M5,12 L19,12");
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
    public static readonly Geometry ChevronLeft = Geometry.Parse("M15,18 L9,12 L15,6");
    public static readonly Geometry ChevronRight = Geometry.Parse("M9,18 L15,12 L9,6");

    // Keyframe state (used for both the Inspector's per-parameter toggle and the transport's prev/next-keyframe
    // composite icons alongside ChevronLeft/ChevronRight above).
    public static readonly Geometry Diamond = Geometry.Parse("M13.06,4.58 L19.42,10.94 A1.5,1.5 0 0 1 19.42,13.06 L13.06,19.42 A1.5,1.5 0 0 1 10.94,19.42 L4.58,13.06 A1.5,1.5 0 0 1 4.58,10.94 L10.94,4.58 A1.5,1.5 0 0 1 13.06,4.58 Z");

    // Inspector / effects. A single Eye glyph is used for both on/off states (color-coded, as the Unicode
    // "👁" it replaces was) rather than swapping to a crossed-out variant.
    public static readonly Geometry Activity = Geometry.Parse("M22,12 L18,12 L15,21 L9,3 L6,12 L2,12");
    public static readonly Geometry Eye = Geometry.Parse("M1,12 s4,-8 11,-8 11,8 11,8 -4,8 -11,8 -11,-8 -11,-8 Z M9,12 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0");

    // Media browser tile fallback.
    public static readonly Geometry Music = Geometry.Parse("M9,18 V5 l12,-2 v13 M3,18 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M15,16 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0");
    public static readonly Geometry Film = Geometry.Parse("M4.18,2 L19.82,2 A2.18,2.18 0 0 1 22,4.18 L22,19.82 A2.18,2.18 0 0 1 19.82,22 L4.18,22 A2.18,2.18 0 0 1 2,19.82 L2,4.18 A2.18,2.18 0 0 1 4.18,2 Z M7,2 L7,22 M17,2 L17,22 M2,12 L22,12 M2,7 L7,7 M2,17 L7,17 M17,17 L22,17 M17,7 L22,7");

    // Timeline canvas drag hint.
    public static readonly Geometry Plus = Geometry.Parse("M12,5 L12,19 M5,12 L19,12");
}
