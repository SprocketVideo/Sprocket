using System.Buffers.Binary;

namespace Sprocket.Audio.Effects;

/// <summary>
/// A minimal RIFF/WAVE reader for impulse-response import (PLAN.md step 49): integer PCM (8/16/24/32-bit) and
/// IEEE float (32/64-bit), any channel count, including <c>WAVE_FORMAT_EXTENSIBLE</c> headers. Pure managed —
/// <c>Sprocket.Audio</c> deliberately has no FFmpeg reference (ARCHITECTURE.md §2), and an IR is a small
/// one-shot file, so a self-contained parser beats routing it through the Media layer. Decodes to interleaved
/// float32 in [-1, 1). Throws <see cref="InvalidDataException"/> for malformed or unsupported files (compressed
/// formats, missing chunks) — callers treat that as "IR unavailable", never as a crash.
/// </summary>
internal static class WaveFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Sanity bounds on the header — anything outside is not a plausible impulse response and is
    /// rejected before a byte of sample data is allocated (a hostile header must not be able to request a
    /// 65535-channel, multi-gigabyte decode).</summary>
    private const int MaxChannels = 8;
    private const int MinSampleRate = 8000;
    private const int MaxSampleRate = 384000;

    /// <summary>Reads the file into interleaved float32 samples, keeping at most <paramref name="maxSeconds"/>
    /// of audio (the rest of the data chunk is never read — the caller trims to that length anyway).</summary>
    public static (float[] Interleaved, int Channels, int SampleRate) Read(string path, double maxSeconds = ImpulseResponse.MaxSeconds)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, maxSeconds);
    }

    /// <summary>Reads a RIFF/WAVE stream into interleaved float32 samples (see <see cref="Read(string, double)"/>).</summary>
    public static (float[] Interleaved, int Channels, int SampleRate) Read(Stream stream, double maxSeconds = ImpulseResponse.MaxSeconds)
    {
        if (!stream.CanSeek)
            throw new InvalidDataException("Not a regular WAVE file (unseekable stream)."); // pipes / device paths
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (ReadTag(reader) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        _ = reader.ReadUInt32(); // RIFF size (unreliable in streamed/oversized files; chunks are walked instead)
        if (ReadTag(reader) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        ushort formatTag = 0, channels = 0, bitsPerSample = 0;
        uint sampleRate = 0;
        bool haveFormat = false;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string tag = ReadTag(reader);
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1); // chunks are word-aligned
            switch (tag)
            {
                case "fmt ":
                    if (size < 16)
                        throw new InvalidDataException("Malformed fmt chunk.");
                    formatTag = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadUInt32();
                    _ = reader.ReadUInt32(); // byte rate
                    _ = reader.ReadUInt16(); // block align
                    bitsPerSample = reader.ReadUInt16();
                    if (formatTag == FormatExtensible && size >= 40)
                    {
                        _ = reader.ReadUInt16(); // cbSize
                        _ = reader.ReadUInt16(); // valid bits per sample
                        _ = reader.ReadUInt32(); // channel mask
                        formatTag = reader.ReadUInt16(); // the sub-format GUID's leading 16 bits are the real tag
                    }
                    if (channels is 0 or > MaxChannels)
                        throw new InvalidDataException($"Unsupported WAVE channel count ({channels}).");
                    if (sampleRate is < MinSampleRate or > MaxSampleRate)
                        throw new InvalidDataException($"Unsupported WAVE sample rate ({sampleRate} Hz).");
                    if (bitsPerSample is not (8 or 16 or 24 or 32 or 64))
                        throw new InvalidDataException($"Unsupported WAVE sample size ({bitsPerSample}-bit).");
                    haveFormat = true;
                    break;
                case "data":
                    if (!haveFormat)
                        throw new InvalidDataException("WAVE data chunk precedes its fmt chunk.");
                    // Bounded by the file AND by the longest IR we keep: a sparse multi-GB file costs nothing.
                    long keep = (long)(maxSeconds * sampleRate) * channels * (bitsPerSample / 8);
                    long available = Math.Min(Math.Min(size, stream.Length - stream.Position), keep);
                    data = reader.ReadBytes((int)Math.Min(available, int.MaxValue));
                    break;
            }
            if (next > stream.Length)
                break;
            stream.Position = next;
        }

        if (!haveFormat || data is null)
            throw new InvalidDataException("WAVE file is missing its fmt or data chunk.");
        if (channels == 0 || sampleRate == 0)
            throw new InvalidDataException("WAVE file declares no channels or sample rate.");

        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample == 0 || data.Length < bytesPerSample)
            throw new InvalidDataException("WAVE file has no samples.");
        int frames = data.Length / (bytesPerSample * channels);
        var samples = new float[frames * channels];
        ReadOnlySpan<byte> bytes = data;

        switch (formatTag, bitsPerSample)
        {
            case (FormatPcm, 8):
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (bytes[i] - 128) / 128f;
                break;
            case (FormatPcm, 16):
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2)) / 32768f;
                break;
            case (FormatPcm, 24):
                for (int i = 0; i < samples.Length; i++)
                {
                    int o = i * 3;
                    int v = bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16);
                    if ((v & 0x800000) != 0)
                        v |= unchecked((int)0xFF000000);
                    samples[i] = v / 8388608f;
                }
                break;
            case (FormatPcm, 32):
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4)) / 2147483648f;
                break;
            case (FormatIeeeFloat, 32):
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4));
                break;
            case (FormatIeeeFloat, 64):
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 8));
                break;
            default:
                throw new InvalidDataException($"Unsupported WAVE format (tag {formatTag}, {bitsPerSample}-bit).");
        }

        return (samples, channels, (int)sampleRate);
    }

    private static string ReadTag(BinaryReader reader)
    {
        Span<byte> tag = stackalloc byte[4];
        if (reader.Read(tag) != 4)
            throw new InvalidDataException("Truncated WAVE header.");
        return System.Text.Encoding.ASCII.GetString(tag);
    }
}
