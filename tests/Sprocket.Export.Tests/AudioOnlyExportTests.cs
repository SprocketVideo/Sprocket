using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Exercises the audio-only delivery path (PLAN.md step 44): exporting the sequence's master mix as sound in each
/// supported format and reopening the result to assert there is no video stream, the audio codec/rate/channels/
/// duration are as selected, and the mix content (muted vs. audible, sub-range) is reflected. Real encode→decode
/// round-trips (pcm/flac/mp3/aac/opus) through the bundled FFmpeg natives.
/// </summary>
public sealed class AudioOnlyExportTests
{
    // format, extension, expected decoded audio-codec name (as ffprobe / the Media probe reports it).
    [Theory]
    [InlineData(ExportAudioFormat.WavPcm, ".wav", "pcm_s16le")]
    [InlineData(ExportAudioFormat.Flac, ".flac", "flac")]
    [InlineData(ExportAudioFormat.Mp3, ".mp3", "mp3")]
    [InlineData(ExportAudioFormat.Aac, ".m4a", "aac")]
    [InlineData(ExportAudioFormat.Opus, ".opus", "opus")]
    public void Export_AudioOnly_RoundTripsWithNoVideoStream(
        ExportAudioFormat format, string extension, string expectedCodec)
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(extension);
        VideoExporter.Export(project, output.Path, new ExportOptions(AudioFormat: format));

        Assert.True(new FileInfo(output.Path).Length > 0);

        ProbedMediaInfo info = MediaSource.ProbeInfo(output.Path);
        Assert.False(info.HasVideo, "an audio-only export must carry no video stream");
        Assert.True(info.HasAudio);
        Assert.Equal(expectedCodec, info.AudioCodec);
        Assert.Equal(ExportFixture.SampleRate, info.SampleRate);
        Assert.Equal(2, info.Channels); // stereo mix default
        // ~1 s. Lossy containers pad/prime a little, so allow a wide bound.
        Assert.InRange(info.Duration.ToSeconds(), 0.8, 1.3);
    }

    [Fact]
    public void Export_AudioOnly_AudibleMix_IsNotSilent()
    {
        // The fixture carries a 440 Hz tone; a WAV (lossless) export of it must read back non-silent.
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(".wav");
        VideoExporter.Export(project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm));

        Assert.True(Rms(output.Path) > 0.01, "an audible tone should export as a non-silent mix");
    }

    [Fact]
    public void Export_AudioOnly_DisabledTrack_ProducesSilence()
    {
        // A disabled (muted) audio track contributes nothing — the file is still written at the full duration but
        // reads back as silence, proving the mix state is reflected in the audio-only render.
        Project project = ExportFixture.BuildProject(withAudio: true);
        foreach (AudioTrack track in project.Timeline.AudioTracks)
            track.Enabled = false;

        using var output = new TempFile(".wav");
        VideoExporter.Export(project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm));

        ProbedMediaInfo info = MediaSource.ProbeInfo(output.Path);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Duration.ToSeconds(), 0.8, 1.3); // still full-length
        Assert.True(Rms(output.Path) < 1e-4, "a muted mix should export as silence");
    }

    [Fact]
    public void Export_AudioOnly_Range_MatchesSelectedDuration()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        // Export only the middle half-second [0.25 s, 0.75 s).
        var range = new ExportRange(Timecode.FromSeconds(0.25), Timecode.FromSeconds(0.75));

        using var output = new TempFile(".wav");
        VideoExporter.Export(project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm),
            sequenceId: null, range);

        ProbedMediaInfo info = MediaSource.ProbeInfo(output.Path);
        Assert.False(info.HasVideo);
        Assert.InRange(info.Duration.ToSeconds(), 0.45, 0.55);
    }

    [Fact]
    public void Export_AudioOnly_Cancellation_RemovesPartialFile()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel(); // cancel before the first sample chunk is written

        using var output = new TempFile(".wav");
        Assert.Throws<OperationCanceledException>(() => VideoExporter.Export(
            project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm),
            sequenceId: null, range: null, progress: null, cancellationToken: cts.Token));

        Assert.False(File.Exists(output.Path), "a cancelled audio-only export leaves no partial file");
    }

    [Fact]
    public void Export_DefaultOptions_StillProducesVideo()
    {
        // AudioFormat is null by default, so the normal A/V (video) path is unchanged.
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(".mp4");
        VideoExporter.Export(project, output.Path, new ExportOptions());

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.True(decoded.Info.HasVideo);
        Assert.True(decoded.Info.HasAudio);
    }

    /// <summary>Root-mean-square amplitude of a decoded audio file's whole mix (interleaved float32).</summary>
    private static double Rms(string path)
    {
        using AudioSource reader = AudioSource.Open(path, ExportFixture.SampleRate, 2);
        double sumSq = 0;
        long n = 0;
        Span<float> buffer = new float[4096];
        int got;
        while ((got = reader.Read(buffer)) > 0)
        {
            for (int i = 0; i < got * reader.Channels; i++)
                sumSq += buffer[i] * (double)buffer[i];
            n += got * reader.Channels;
        }
        return n == 0 ? 0 : Math.Sqrt(sumSq / n);
    }
}
