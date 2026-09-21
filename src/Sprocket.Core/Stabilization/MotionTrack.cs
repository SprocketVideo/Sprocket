using Sprocket.Core.Timing;

namespace Sprocket.Core.Stabilization;

/// <summary>One tracked inlier feature position, in the same width-normalised coordinates as
/// <see cref="FrameMotion"/> (fraction of the analysis width). Stored only when the track carries the
/// optional points section (used by the Show Track Points overlay, phase 6).</summary>
public readonly record struct FeaturePoint(float X, float Y);

/// <summary>
/// The recovered per-source camera-motion track (plan/features/stabilization.md): the sequence of
/// inter-frame <see cref="FrameMotion"/> estimates over one analysed source range, plus the identity of the
/// source it was analysed from and the analysis settings that shaped it. Produced once by the analyzer
/// (<c>Sprocket.Analysis</c>, phase 3), cached per user, and consumed by the <see cref="StabilizationSolver"/>.
///
/// <para><b>Serialisation.</b> <see cref="Write"/> / <see cref="Read"/> use a compact little-endian binary
/// format (magic <c>SPMT</c> + version) so the cache file is small and fast to load; the reader version-guards
/// so a stale cache from an older build is rejected (and re-analysed) rather than replayed wrongly. The
/// optional points section is format v2 — a v1 reader/track simply carries no points, and <see cref="Read"/>
/// accepts both.</para>
/// </summary>
public sealed class MotionTrack
{
    /// <summary>The file magic, the ASCII bytes <c>S P M T</c>.</summary>
    public static readonly byte[] Magic = "SPMT"u8.ToArray();

    /// <summary>The current on-disk format version. Bump when the layout changes incompatibly so old caches
    /// read as invalid instead of being misinterpreted.</summary>
    public const int FormatVersion = 2;

    /// <summary>
    /// Builds a motion track. <paramref name="framePts"/> and <paramref name="motions"/> must be the same
    /// length (one entry per analysed frame; the first motion is <see cref="FrameMotion.Identity"/>). When
    /// <paramref name="points"/> is non-null it must also match that length.
    /// </summary>
    public MotionTrack(
        string sourceIdentity,
        bool detailedAnalysis,
        Timecode rangeStart,
        Timecode rangeEnd,
        Rational frameRate,
        IReadOnlyList<long> framePts,
        IReadOnlyList<FrameMotion> motions,
        IReadOnlyList<IReadOnlyList<FeaturePoint>>? points = null)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(framePts);
        ArgumentNullException.ThrowIfNull(motions);
        if (framePts.Count != motions.Count)
            throw new ArgumentException("framePts and motions must have the same length.", nameof(motions));
        if (points is not null && points.Count != motions.Count)
            throw new ArgumentException("points must have one entry per frame.", nameof(points));

        SourceIdentity = sourceIdentity;
        DetailedAnalysis = detailedAnalysis;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        FrameRate = frameRate;
        FramePts = framePts;
        Motions = motions;
        Points = points;
    }

    /// <summary>An opaque, stable identity for the analysed source (the App builds it from path + size + mtime,
    /// like the render-cache hasher). Part of what keys the analysis cache — see <see cref="AnalysisKey"/>.</summary>
    public string SourceIdentity { get; }

    /// <summary>Whether this track came from the higher-resolution Detailed Analysis pass.</summary>
    public bool DetailedAnalysis { get; }

    /// <summary>The analysed source range (inclusive of the ± handles the analyzer rounds out to).</summary>
    public Timecode RangeStart { get; }

    /// <summary>The end of the analysed source range.</summary>
    public Timecode RangeEnd { get; }

    /// <summary>The source frame rate the track was sampled at.</summary>
    public Rational FrameRate { get; }

    /// <summary>Each analysed frame's source presentation time in ticks (<see cref="Timecode.TicksPerSecond"/>),
    /// ascending — the solver binary-searches this to map a render's source time to a track index.</summary>
    public IReadOnlyList<long> FramePts { get; }

    /// <summary>The inter-frame motion at each frame (index <c>i</c> = motion from frame <c>i-1</c> to <c>i</c>;
    /// index 0 is <see cref="FrameMotion.Identity"/>).</summary>
    public IReadOnlyList<FrameMotion> Motions { get; }

    /// <summary>The optional per-frame tracked inlier positions (format v2), or <see langword="null"/> when the
    /// track carries none.</summary>
    public IReadOnlyList<IReadOnlyList<FeaturePoint>>? Points { get; }

    /// <summary>The number of analysed frames.</summary>
    public int FrameCount => FramePts.Count;

    /// <summary>Writes the track to <paramref name="stream"/> in the binary cache format.</summary>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);

        byte flags = 0;
        if (DetailedAnalysis) flags |= 0x01;
        if (Points is not null) flags |= 0x02;
        writer.Write(flags);

        writer.Write(SourceIdentity);
        writer.Write(RangeStart.Ticks);
        writer.Write(RangeEnd.Ticks);
        writer.Write(FrameRate.Num);
        writer.Write(FrameRate.Den);
        writer.Write(FrameCount);

        for (int i = 0; i < FrameCount; i++)
        {
            writer.Write(FramePts[i]);
            WriteMotion(writer, Motions[i]);
        }

        if (Points is not null)
        {
            for (int i = 0; i < FrameCount; i++)
            {
                IReadOnlyList<FeaturePoint> frame = Points[i];
                writer.Write(frame.Count);
                for (int p = 0; p < frame.Count; p++)
                {
                    writer.Write(frame[p].X);
                    writer.Write(frame[p].Y);
                }
            }
        }
    }

    /// <summary>Reads a track written by <see cref="Write"/>. Throws <see cref="InvalidDataException"/> when the
    /// magic or version don't match (a stale/foreign file), so callers treat it as a cache miss.</summary>
    public static MotionTrack Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        Span<byte> magic = stackalloc byte[Magic.Length];
        int read = reader.Read(magic);
        if (read != Magic.Length || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a Sprocket motion track (bad magic).");

        int version = reader.ReadInt32();
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"Unsupported motion-track version {version}.");

        byte flags = reader.ReadByte();
        bool detailed = (flags & 0x01) != 0;
        bool hasPoints = (flags & 0x02) != 0;

        string sourceIdentity = reader.ReadString();
        long rangeStart = reader.ReadInt64();
        long rangeEnd = reader.ReadInt64();
        int num = reader.ReadInt32();
        int den = reader.ReadInt32();
        int frameCount = reader.ReadInt32();
        if (frameCount < 0)
            throw new InvalidDataException("Negative frame count.");

        var framePts = new long[frameCount];
        var motions = new FrameMotion[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            framePts[i] = reader.ReadInt64();
            motions[i] = ReadMotion(reader);
        }

        IReadOnlyList<FeaturePoint>[]? points = null;
        if (hasPoints)
        {
            points = new IReadOnlyList<FeaturePoint>[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                int count = reader.ReadInt32();
                if (count < 0)
                    throw new InvalidDataException("Negative point count.");
                var frame = new FeaturePoint[count];
                for (int p = 0; p < count; p++)
                    frame[p] = new FeaturePoint(reader.ReadSingle(), reader.ReadSingle());
                points[i] = frame;
            }
        }

        return new MotionTrack(
            sourceIdentity, detailed, new Timecode(rangeStart), new Timecode(rangeEnd),
            new Rational(num, den), framePts, motions, points);
    }

    private static void WriteMotion(BinaryWriter writer, FrameMotion m)
    {
        writer.Write(m.Tx);
        writer.Write(m.Ty);
        writer.Write(m.LogScale);
        writer.Write(m.Angle);
        Homography h = m.Homography;
        writer.Write(h.M00); writer.Write(h.M01); writer.Write(h.M02);
        writer.Write(h.M10); writer.Write(h.M11); writer.Write(h.M12);
        writer.Write(h.M20); writer.Write(h.M21);
        writer.Write(m.Confidence);
        writer.Write(m.FeatureCount);
    }

    private static FrameMotion ReadMotion(BinaryReader reader)
    {
        double tx = reader.ReadDouble();
        double ty = reader.ReadDouble();
        double logScale = reader.ReadDouble();
        double angle = reader.ReadDouble();
        var h = new Homography(
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
            reader.ReadDouble(), reader.ReadDouble());
        double confidence = reader.ReadDouble();
        int featureCount = reader.ReadInt32();
        return new FrameMotion(tx, ty, logScale, angle, h, confidence, featureCount);
    }
}
