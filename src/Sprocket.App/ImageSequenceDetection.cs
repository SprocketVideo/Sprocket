using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sprocket.App;

/// <summary>A detected contiguous numbered image run (PLAN.md step 42): the printf pattern the <c>image2</c>
/// demuxer expands, the first frame's number and the frame count, and the resolved first-file path.</summary>
/// <param name="Pattern">Absolute printf-style pattern (e.g. <c>C:\shot\frame_%04d.png</c>).</param>
/// <param name="StartNumber">The number of the first frame in the run.</param>
/// <param name="FrameCount">The count of contiguous frames.</param>
/// <param name="FirstFilePath">The absolute path of the first frame (the <see cref="MediaRef"/>'s stored path).</param>
public readonly record struct ImageSequenceInfo(string Pattern, int StartNumber, int FrameCount, string FirstFilePath);

/// <summary>
/// Pure, IO-light detection of a numbered image sequence from one picked file (PLAN.md step 42). Given a picked
/// file and its sibling file names, it finds the contiguous run of same-named, same-padded, consecutively-numbered
/// frames containing the picked file — refusing gaps and mixed zero-padding (the run stops at the first
/// discontinuity), matching the Premiere/Resolve "detect image sequence" behaviour. The core overload takes the
/// sibling names as data so it is headlessly testable; a convenience overload reads the directory.
/// </summary>
public static partial class ImageSequenceDetection
{
    /// <summary>Image extensions Sprocket imports as stills / sequences. EXR is deliberately excluded (BtbN gpl
    /// decoder coverage is unreliable, PLAN.md step 42 risks).</summary>
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".tga", ".webp", ".dpx"];

    /// <summary>Whether <paramref name="path"/> has a recognised image extension.</summary>
    public static bool IsImagePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string ext = Path.GetExtension(path);
        return ImageExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    // Splits a file name into (prefix, trailing-digit-run, suffix). The lazy prefix forces the digit group to be
    // the LAST run of digits in the name (any earlier digits are absorbed into the prefix, since the suffix can
    // hold none), which is the frame number by convention (e.g. "shot2_frame_0012.png" → "shot2_frame_", "0012",
    // ".png").
    [GeneratedRegex(@"^(.*?)(\d+)(\D*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();

    /// <summary>
    /// Detects the numbered run containing <paramref name="pickedPath"/> among <paramref name="siblingFileNames"/>
    /// (file names in the same directory, no paths). Returns <see langword="null"/> when the picked file isn't
    /// numbered or no contiguous run of at least two frames exists (i.e. it's a single still).
    /// </summary>
    public static ImageSequenceInfo? Detect(string pickedPath, IEnumerable<string> siblingFileNames)
    {
        ArgumentNullException.ThrowIfNull(pickedPath);
        ArgumentNullException.ThrowIfNull(siblingFileNames);

        string pickedName = Path.GetFileName(pickedPath);
        Match m = NumberedName().Match(pickedName);
        if (!m.Success)
            return null;

        string prefix = m.Groups[1].Value;
        string pickedDigits = m.Groups[2].Value;
        string suffix = m.Groups[3].Value;
        int width = pickedDigits.Length;
        if (!int.TryParse(pickedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out int pickedNumber))
            return null; // more digits than fit an int — not a frame index we can model

        // Collect every sibling that shares the prefix/suffix and exact digit width (mixed padding is refused).
        var numbers = new SortedSet<int>();
        foreach (string name in siblingFileNames)
        {
            Match sm = NumberedName().Match(name);
            if (!sm.Success)
                continue;
            if (sm.Groups[2].Value.Length != width)
                continue;
            if (!string.Equals(sm.Groups[1].Value, prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(sm.Groups[3].Value, suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(sm.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                numbers.Add(n);
        }

        if (!numbers.Contains(pickedNumber))
            numbers.Add(pickedNumber);

        // Expand a contiguous run outward from the picked number (stop at the first gap in either direction).
        int start = pickedNumber;
        while (numbers.Contains(start - 1))
            start--;
        int end = pickedNumber;
        while (numbers.Contains(end + 1))
            end++;

        int count = end - start + 1;
        if (count < 2)
            return null; // a lone numbered file is a single still, not a sequence

        string? dir = Path.GetDirectoryName(pickedPath);
        string pattern = $"{prefix}%0{width}d{suffix}";
        string firstName = $"{prefix}{start.ToString($"D{width}", CultureInfo.InvariantCulture)}{suffix}";
        string patternPath = dir is null ? pattern : Path.Combine(dir, pattern);
        string firstPath = dir is null ? firstName : Path.Combine(dir, firstName);
        return new ImageSequenceInfo(patternPath, start, count, firstPath);
    }

    /// <summary>Reads the picked file's directory from disk and detects a sequence (returns <see langword="null"/>
    /// when the directory can't be read or no run is found).</summary>
    public static ImageSequenceInfo? Detect(string pickedPath)
    {
        string? dir = Path.GetDirectoryName(pickedPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;
        try
        {
            IEnumerable<string> names = Directory.EnumerateFiles(dir).Select(Path.GetFileName)!;
            return Detect(pickedPath, names.Where(n => n is not null)!);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
