using System;
using System.Globalization;
using System.Linq;

namespace Sprocket.App;

/// <summary>
/// A tiny SemVer-ish version for update discovery (PLAN.md step 45), parsed from release tags like
/// <c>v0.2.0</c> / <c>0.2.0-alpha.1</c> over the same shape <see cref="Program.AppVersion"/> reports.
/// Understands an optional leading <c>v</c>, a dotted <c>major.minor.patch</c> core, an optional
/// prerelease label (<c>-alpha.1</c>), and ignores build metadata (<c>+sha</c>). Ordering follows
/// SemVer 2.0 §11: numeric parts first; a prerelease sorts below its release; prerelease identifiers
/// compare numerically when both are numeric, ordinally otherwise, numeric below alphanumeric.
/// Malformed tags are rejected by <see cref="TryParse"/> rather than throwing, so a bad GitHub
/// release can never break startup.
/// </summary>
public readonly struct UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    private readonly string[]? _prerelease; // null or empty = a stable release

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Whether this version carries a prerelease label (<c>-alpha.1</c> etc.).</summary>
    public bool IsPrerelease => _prerelease is { Length: > 0 };

    private UpdateVersion(int major, int minor, int patch, string[]? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    /// <summary>
    /// Parses <paramref name="text"/> (a git tag or <see cref="Program.AppVersion"/> string). Returns
    /// <see langword="false"/> for anything that isn't <c>[v]major.minor.patch[-prerelease][+meta]</c>.
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        int plus = s.IndexOf('+'); // build metadata never participates in ordering
        if (plus >= 0)
            s = s[..plus];

        int dash = s.IndexOf('-');
        ReadOnlySpan<char> core = dash >= 0 ? s[..dash] : s;
        ReadOnlySpan<char> label = dash >= 0 ? s[(dash + 1)..] : default;

        Span<Range> parts = stackalloc Range[4];
        if (core.Split(parts, '.') != 3)
            return false;
        if (!TryParseNumber(core[parts[0]], out int major) ||
            !TryParseNumber(core[parts[1]], out int minor) ||
            !TryParseNumber(core[parts[2]], out int patch))
            return false;

        string[]? prerelease = null;
        if (dash >= 0)
        {
            if (label.IsEmpty)
                return false; // "1.2.3-" is malformed
            prerelease = label.ToString().Split('.');
            foreach (string id in prerelease)
            {
                if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return false;
            }
        }

        version = new UpdateVersion(major, minor, patch, prerelease);
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <inheritdoc />
    public int CompareTo(UpdateVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // Same numeric core: a release outranks any of its prereleases.
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        string[] a = _prerelease!, b = other._prerelease!;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            c = CompareIdentifier(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length); // "alpha" < "alpha.1"
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool aNum = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long an);
        bool bNum = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long bn);
        if (aNum && bNum) return an.CompareTo(bn);
        if (aNum) return -1; // numeric identifiers sort below alphanumeric ones
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator ==(UpdateVersion left, UpdateVersion right) => left.Equals(right);
    public static bool operator !=(UpdateVersion left, UpdateVersion right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UpdateVersion v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, IsPrerelease ? string.Join('.', _prerelease!) : "");

    /// <inheritdoc />
    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', _prerelease!)}" : $"{Major}.{Minor}.{Patch}";
}
