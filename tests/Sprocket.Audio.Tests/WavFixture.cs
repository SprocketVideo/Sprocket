using System.Buffers.Binary;

namespace Sprocket.Audio.Tests;

/// <summary>Writes small RIFF/WAVE fixtures (16-bit PCM or 32-bit float) for the impulse-response tests
/// (PLAN.md step 49) into a per-test temp folder.</summary>
internal static class WavFixture
{
    public static string TempPath(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static void Write(string path, float[] interleaved, int channels, int sampleRate, bool asFloat = false)
    {
        int bytesPerSample = asFloat ? 4 : 2;
        int dataBytes = interleaved.Length * bytesPerSample;
        var data = new byte[dataBytes];
        for (int i = 0; i < interleaved.Length; i++)
        {
            if (asFloat)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), interleaved[i]);
            else
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2),
                    (short)Math.Clamp(MathF.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue));
        }

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((ushort)(asFloat ? 3 : 1));
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);
        w.Write((ushort)(channels * bytesPerSample));
        w.Write((ushort)(bytesPerSample * 8));
        w.Write("data"u8);
        w.Write(dataBytes);
        w.Write(data);
    }
}
