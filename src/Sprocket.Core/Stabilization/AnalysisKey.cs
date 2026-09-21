using System.Security.Cryptography;
using System.Text;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Stabilization;

/// <summary>
/// The identity of one cached motion-analysis result (plan/features/stabilization.md): the source it was
/// analysed from, whether it was the Detailed pass, and the <b>bucketed</b> source range. The range is
/// bucketed so that small trims re-use an existing analysis rather than forcing a re-analyse — a clip's
/// in/out points are padded by <see cref="HandleSeconds"/> of handles and rounded outward to
/// <see cref="BucketSeconds"/> boundaries (see <see cref="ForClipRange"/>).
///
/// <para>Analysis lives in a per-user cache keyed by <see cref="CacheFileName"/> (a content hash), so the
/// same footage analysed in two projects shares one cache entry, Resolve/FCP style. Only the smoothing/framing
/// solve parameters change per use — they never invalidate the analysis, only the (cheap) solve.</para>
/// </summary>
public sealed record AnalysisKey(string SourceIdentity, bool Detailed, Timecode RangeStart, Timecode RangeEnd)
{
    /// <summary>Bumped when the key scheme changes, so entries from an older build get fresh keys.</summary>
    public const int KeyVersion = 1;

    /// <summary>Handles padded onto each side of a clip's used range before analysis, in seconds — so a later
    /// small trim still falls inside the analysed span.</summary>
    public const int HandleSeconds = 2;

    /// <summary>The range-bucket granularity in seconds — the padded range is rounded outward to a multiple of
    /// this, so trims within a bucket re-use the same analysis.</summary>
    public const int BucketSeconds = 5;

    /// <summary>
    /// The key for analysing a clip whose used source range is [<paramref name="sourceIn"/>,
    /// <paramref name="sourceOut"/>): pad by <see cref="HandleSeconds"/> and round outward to
    /// <see cref="BucketSeconds"/> boundaries (clamped at 0). Two trims within the same bucket produce the same
    /// key, so the cached track is re-used.
    /// </summary>
    public static AnalysisKey ForClipRange(
        string sourceIdentity, bool detailed, Timecode sourceIn, Timecode sourceOut)
    {
        (Timecode start, Timecode end) = BucketRange(sourceIn, sourceOut);
        return new AnalysisKey(sourceIdentity, detailed, start, end);
    }

    /// <summary>The padded, outward-rounded analysis range for a used span [<paramref name="sourceIn"/>,
    /// <paramref name="sourceOut"/>] — exposed so the analyzer knows exactly which span to decode.</summary>
    public static (Timecode Start, Timecode End) BucketRange(Timecode sourceIn, Timecode sourceOut)
    {
        long bucket = (long)BucketSeconds * Timecode.TicksPerSecond;
        long handle = (long)HandleSeconds * Timecode.TicksPerSecond;

        long lo = Math.Min(sourceIn.Ticks, sourceOut.Ticks) - handle;
        long hi = Math.Max(sourceIn.Ticks, sourceOut.Ticks) + handle;

        long start = FloorDiv(lo, bucket) * bucket;
        long end = CeilDiv(hi, bucket) * bucket;
        if (start < 0)
            start = 0;
        if (end <= start)
            end = start + bucket;
        return (new Timecode(start), new Timecode(end));
    }

    /// <summary>A stable, filesystem-safe cache file name for this key (a lowercase-hex SHA-256 over the key
    /// fields, plus the <c>.spmt</c> extension). Deterministic across processes and machines for the same
    /// source identity and range.</summary>
    public string CacheFileName => $"{ContentHash()}.spmt";

    private string ContentHash()
    {
        string canonical = $"{KeyVersion}|{SourceIdentity}|{(Detailed ? 1 : 0)}|{RangeStart.Ticks}|{RangeEnd.Ticks}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor((double)a / b);

    private static long CeilDiv(long a, long b) => (long)Math.Ceiling((double)a / b);
}
