using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Real encode→decode tests for the resolution / frame-rate overrides a preset carries (PLAN.md step 29 presets).
/// The fixture is 320×240 @ 30 fps for 1 s; overriding the output rate resamples the timeline (more / fewer frames)
/// and overriding the resolution encodes at the chosen (4K-capped, even-rounded) size — both asserted by reopening
/// the file and probing it. Overrides default to <see langword="null"/>, so an export with none is byte-for-byte the
/// pre-step-29 behaviour (covered elsewhere).
/// </summary>
public sealed class ResolutionFrameRateExportTests
{
    [Fact]
    public void FrameRateOverride_ResamplesTheTimelineToTheTargetRate()
    {
        Project project = ExportFixture.BuildProject(withAudio: false); // 1 s @ 30 fps → ~30 frames native

        using var native = new TempFile();
        using var half = new TempFile();
        using var doubled = new TempFile();

        VideoExporter.Export(project, native.Path);
        VideoExporter.Export(project, half.Path, new ExportOptions(FrameRate: new Rational(15, 1)));
        VideoExporter.Export(project, doubled.Path, new ExportOptions(FrameRate: new Rational(60, 1)));

        int nativeFrames = ExportProbe.CountVideoFrames(native.Path);
        int halfFrames = ExportProbe.CountVideoFrames(half.Path);
        int doubledFrames = ExportProbe.CountVideoFrames(doubled.Path);

        Assert.InRange(nativeFrames, 28, 32);
        Assert.InRange(halfFrames, 13, 17);      // ~15 frames at 15 fps over 1 s
        Assert.InRange(doubledFrames, 57, 63);   // ~60 frames at 60 fps over 1 s
        Assert.True(halfFrames < nativeFrames && nativeFrames < doubledFrames,
            $"frame count should track the output rate: 15fps={halfFrames}, 30fps={nativeFrames}, 60fps={doubledFrames}");
    }

    [Fact]
    public void ResolutionOverride_EncodesAtTheChosenSize()
    {
        Project project = ExportFixture.BuildProject(withAudio: false); // native 320×240

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path, new ExportOptions(Resolution: new Resolution(160, 120)));

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal(160, decoded.Info.Width);
        Assert.Equal(120, decoded.Info.Height);
    }

    [Fact]
    public void ResolutionOverride_RoundsToAnEvenSize_SoA420CodecAccepts()
    {
        // An odd requested size is rounded down to even by the same cap logic the sequence resolution uses.
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path, new ExportOptions(Resolution: new Resolution(641, 481)));

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal(640, decoded.Info.Width);
        Assert.Equal(480, decoded.Info.Height);
    }

    [Fact]
    public void PresetToOptions_DrivesTheExport_EndToEnd()
    {
        // A preset with a resolution + rate, applied straight through ToOptions(), produces a file at that size and
        // (roughly) that frame count — the whole preset → options → encode path.
        Project project = ExportFixture.BuildProject(withAudio: false);
        var preset = new ExportPreset(
            "test-720p60",
            new ExportFormat(ExportContainer.Mp4, ExportVideoCodec.H264, ExportAudioCodec.Aac),
            ExportQuality.Medium,
            new Resolution(1280, 720),
            new Rational(60, 1));

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path, preset.ToOptions());

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal(1280, decoded.Info.Width);
        Assert.Equal(720, decoded.Info.Height);
        Assert.InRange(ExportProbe.CountVideoFrames(output.Path), 57, 63);
    }
}
