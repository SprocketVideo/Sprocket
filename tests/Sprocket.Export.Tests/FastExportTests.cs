using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Fast Export (export-speed phase 3): the mode reaches the encoder request and the summary, sources open with GPU
/// decode where available, and a GPU decoder that fails mid-export falls back to software without dropping or
/// repeating a frame. CI has no GPU, so real exports assert only self-consistent reporting (never that hardware
/// engaged); the fallback is exercised through the provider's fault-injection seam over the software fixture.
/// </summary>
public sealed class FastExportTests
{
    [Fact]
    public void FastExport_WritesAValidFile_AndReportsTheModeAndDecodePath()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile();
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, new ExportOptions(Mode: ExportMode.Fast), sequenceId: null, range: null);

        Assert.Equal(ExportMode.Fast, summary.Mode);
        // Fast always probes the GPU encoders, whatever the Encoding option says.
        Assert.Equal(ExportAcceleration.Hardware, summary.RequestedAcceleration);
        Assert.Equal(!summary.HardwareVideoEngaged, summary.FellBackToSoftware);

        // A software fallback runs the speed-first preset; a GPU encoder takes no preset.
        Assert.Equal(summary.HardwareVideoEngaged ? null : "veryfast", summary.SoftwarePreset);

        // Software decode unless the GPU-decode opt-in is set in this environment.
        ExportDecodeSummary decode = summary.Decode;
        Assert.Equal(ExportGpuDecode.OptedIn, decode.HardwareRequested);
        Assert.Equal(1, decode.Sources); // one media source, however many workers opened it
        Assert.Equal(0, decode.FallbackSources);

        Assert.InRange(summary.VideoFrames, 28, 32);
        Assert.Equal(summary.VideoFrames, ExportProbe.CountVideoFrames(output.Path));
    }

    [Fact]
    public void FastExport_WithGpuDecodeOptIn_ReportsAConsistentDecodePath()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile();
        ExportRunSummary summary = VideoExporter.ExportCore(
            project, output.Path, new ExportOptions(Mode: ExportMode.Fast), null, null, null, default, null,
            pipelined: true, renderWorkers: 2, gpuDecodeOverride: true);

        ExportDecodeSummary decode = summary.Decode;
        Assert.True(decode.HardwareRequested);
        Assert.Equal(1, decode.Sources); // one source opened by two workers counts once
        Assert.Equal(0, decode.FallbackSources);
        Assert.Equal(decode.HardwareSources > 0, decode.HardwareDevice is not null);
        Assert.Equal(HardwareAccelSettings.ForceSoftware, decode.DisabledByUser);
        Assert.Equal(summary.VideoFrames, ExportProbe.CountVideoFrames(output.Path));
    }

    [Fact]
    public void FinalExport_RunsTheCodecDefaultPreset()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile();
        ExportRunSummary summary = VideoExporter.ExportWithSummary(project, output.Path, default, null, null);

        Assert.Equal("medium", summary.SoftwarePreset);
    }

    [Fact]
    public void FinalExport_DecodesEverySourceInSoftware()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile();
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, new ExportOptions(Mode: ExportMode.Final), sequenceId: null, range: null);

        Assert.Equal(ExportMode.Final, summary.Mode);
        Assert.Equal(ExportAcceleration.Software, summary.RequestedAcceleration);
        Assert.Equal(new ExportDecodeSummary(false, 0, 1, 0, null), summary.Decode);
    }

    [Fact]
    public void AudioOnlyExport_ReportsNoDecode()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(".wav");
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm, Mode: ExportMode.Fast),
            sequenceId: null, range: null);

        Assert.Equal(0, summary.Decode.Sources);
    }

    /// <summary>A forward walk, a backward cut (seek), a stretch in reverse (GOP window refills), and forward again —
    /// the same request mix the prefetch parity test uses, so every provider operation can host the fault.</summary>
    private static List<(Timecode Time, bool Reverse)> Requests()
    {
        ProbedMediaInfo info = ExportFixture.Probe();
        var requests = new List<(Timecode, bool)>();
        for (int i = 0; i < 20; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));
        for (int i = 5; i < 12; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));
        for (int i = 25; i > 14; i--) requests.Add((Timecode.FromFrames(i, info.FrameRate), true));
        for (int i = 3; i < 9; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));
        // Back into reverse after the forward walk: a fallback during that walk must not leave the GOP window reading
        // the failed source.
        for (int i = 14; i > 8; i--) requests.Add((Timecode.FromFrames(i, info.FrameRate), true));
        return requests;
    }

    private static List<long> Walk(ExportFrameProvider provider, List<(Timecode Time, bool Reverse)> requests)
    {
        var pts = new List<long>(requests.Count);
        foreach ((Timecode time, bool reverse) in requests)
            pts.Add(provider.GetFrame(time, reverse)?.Pts.Ticks ?? -1);
        return pts;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // with prefetch, a forward fault fires on the background decode task
    public void GpuDecodeFault_AtAnyOperation_FallsBackToSoftware_WithoutDroppingOrRepeatingFrames(bool prefetch)
    {
        List<(Timecode Time, bool Reverse)> requests = Requests();

        List<long> expected;
        using (var reference = new ExportFrameProvider(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled)))
            expected = Walk(reference, requests);

        // Count the walk's seek / decode / refill operations, then fail each one in turn — the opening seek, forward
        // decodes, the backward cut's seek, reverse window refills, and the final forward walk.
        int total = 0;
        using (ExportFrameProvider counting = Open())
        {
            counting.DecodeFaultForTests = () => Interlocked.Increment(ref total);
            Walk(counting, requests);
        } // dispose waits out the trailing prefetch, so it is counted
        Assert.True(total > 30, $"expected a substantial walk, got {total} operations");

        for (int faultAt = 1; faultAt <= total; faultAt++)
        {
            ExportFrameProvider provider = Open();
            int operations = 0;
            int target = faultAt;
            provider.AssumeHardwareForTests = true;
            provider.DecodeFaultForTests = () =>
            {
                if (Interlocked.Increment(ref operations) == target)
                    throw new InvalidOperationException("simulated GPU decode failure");
            };

            List<long> actual = Walk(provider, requests);
            provider.Dispose(); // waits out a trailing prefetch — the walk's last operation — before asserting

            Assert.True(provider.FellBackToSoftware, $"fault at operation {faultAt} did not fall back");
            Assert.False(provider.DecodedOnHardware);
            Assert.True(expected.SequenceEqual(actual), $"fault at operation {faultAt} changed the served frames");
        }

        ExportFrameProvider Open() =>
            new(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled), prefetch: prefetch);
    }

    [Fact]
    public void SoftwareDecodeFault_Propagates()
    {
        // Only a GPU decoder gets the software reopen; a software source's fault is a real error.
        using var provider = new ExportFrameProvider(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled));
        provider.DecodeFaultForTests = () => throw new InvalidOperationException("software decode failure");

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetFrame(Timecode.Zero));
        Assert.Equal("software decode failure", ex.Message);
        Assert.False(provider.FellBackToSoftware);
    }

    [Fact]
    public void DecodeFacts_MergeWorkerInstancesPerSource()
    {
        var a = new MediaRefId(Guid.NewGuid());
        var b = new MediaRefId(Guid.NewGuid());
        var c = new MediaRefId(Guid.NewGuid());
        var facts = new VideoExporter.DecodeFacts();

        facts.Add(a, decodedOnHardware: true, fellBack: false, "cuda");  // a: hardware on both workers
        facts.Add(a, decodedOnHardware: true, fellBack: false, "cuda");
        facts.Add(b, decodedOnHardware: true, fellBack: false, "cuda");  // b: one worker's decoder failed mid-export
        facts.Add(b, decodedOnHardware: false, fellBack: true, "cuda");
        facts.Add(c, decodedOnHardware: false, fellBack: false, null);   // c: no GPU decoder for its codec

        ExportDecodeSummary summary = facts.ToSummary(requested: true);
        Assert.Equal(1, summary.HardwareSources);
        Assert.Equal(2, summary.SoftwareSources);
        Assert.Equal(1, summary.FallbackSources);
        Assert.Equal("cuda", summary.HardwareDevice);
        Assert.Equal(3, summary.Sources);
    }

    [Fact]
    public void DecodeFacts_DeviceIsNamedForFallbacks_ButNotForPlainSoftware()
    {
        var fellBack = new VideoExporter.DecodeFacts();
        fellBack.Add(new MediaRefId(Guid.NewGuid()), decodedOnHardware: false, fellBack: true, "d3d11va");
        ExportDecodeSummary summary = fellBack.ToSummary(requested: true);
        Assert.Equal("d3d11va", summary.HardwareDevice); // the device that failed mid-export
        Assert.Equal(1, summary.FallbackSources);

        var software = new VideoExporter.DecodeFacts();
        software.Add(new MediaRefId(Guid.NewGuid()), decodedOnHardware: false, fellBack: false, null);
        Assert.Null(software.ToSummary(requested: true).HardwareDevice);
    }
}
