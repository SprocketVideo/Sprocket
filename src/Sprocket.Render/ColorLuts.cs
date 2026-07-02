using SkiaSharp;
using Sprocket.Core.Model;

namespace Sprocket.Render;

/// <summary>
/// The bundled camera-vendor 3D LUTs behind the input color transform (PLAN.md step 37): DJI's official
/// D-Log / D-Log M → Rec.709 <c>.cube</c> files (downloaded from dji.com — see <c>Luts/NOTICE.md</c>),
/// embedded so the transform is byte-identical across OSes like the title fonts. Each LUT is parsed and
/// packed into its 2D GPU texture (<see cref="CubeLut.ToPackedImage"/>) once on first use and cached for
/// the process lifetime — never per frame. Profile ids/indices come from <see cref="ColorProfiles"/>.
/// </summary>
public static class ColorLuts
{
    private static readonly Dictionary<string, (SKImage Image, int Size)> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    private static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
    {
        [ColorProfiles.DjiDLog] = "DJI-DLog-to-Rec709.cube",
        [ColorProfiles.DjiDLogM] = "DJI-DLogM-to-Rec709.cube",
    };

    /// <summary>
    /// The packed LUT texture + lattice size for a <see cref="EffectParamNames.SourceProfile"/> value.
    /// Returns <see langword="false"/> for an unknown profile or a missing/broken asset (the transform
    /// then passes through rather than crashing the frame, §15). Callers must not dispose the image —
    /// it is shared for the process lifetime.
    /// </summary>
    public static bool TryGet(double sourceProfile, out SKImage image, out int size)
    {
        string profile = ColorProfiles.FromIndex(sourceProfile);
        lock (Gate)
        {
            if (Cache.TryGetValue(profile, out (SKImage Image, int Size) cached))
            {
                (image, size) = cached;
                return true;
            }

            if (Resources.TryGetValue(profile, out string? resource))
            {
                try
                {
                    using Stream? stream = typeof(ColorLuts).Assembly
                        .GetManifestResourceStream($"Sprocket.Render.Luts.{resource}");
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        CubeLut lut = CubeLut.Parse(reader.ReadToEnd());
                        (image, size) = (lut.ToPackedImage(), lut.Size);
                        Cache[profile] = (image, size);
                        return true;
                    }
                }
                catch
                {
                    // A broken embedded asset degrades to pass-through; fall out to the failure return.
                }
            }
        }
        image = null!;
        size = 0;
        return false;
    }
}
