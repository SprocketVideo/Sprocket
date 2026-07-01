using Sprocket.App;
using Sprocket.Media;
using Sprocket.Playback;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>Covers the pure status-bar telemetry formatting (UI.md §3.7): the engine-state label with its
/// GPU/hardware-accel decode path, and the fps · resolution · duration readout.</summary>
public class StatusBarFormatTests
{
    [Theory]
    [InlineData(PlaybackState.Stopped, "Ready")]
    [InlineData(PlaybackState.Paused, "Paused")]
    [InlineData(PlaybackState.Playing, "Playing")]
    public void EngineLabel_no_decode_is_just_the_state_word(PlaybackState state, string expected) =>
        Assert.Equal(expected, StatusBarFormat.EngineLabel(state, decode: null));

    [Fact]
    public void EngineLabel_hardware_decode_shows_GPU_and_uppercased_device()
    {
        var decode = new VideoDecodeInfo("hevc", "d3d11va");
        Assert.Equal("Playing · GPU · D3D11VA", StatusBarFormat.EngineLabel(PlaybackState.Playing, decode));
    }

    [Fact]
    public void EngineLabel_software_decode_shows_CPU_path()
    {
        var decode = new VideoDecodeInfo("h264", HardwareDeviceName: null);
        Assert.Equal("Ready · CPU · software", StatusBarFormat.EngineLabel(PlaybackState.Stopped, decode));
    }

    [Fact]
    public void Telemetry_formats_fps_resolution_and_duration()
    {
        Assert.Equal("23.98 fps · 1920×1080 · 00:01:36:00",
            StatusBarFormat.Telemetry(23.976, 1920, 1080, "00:01:36:00"));
    }

    [Fact]
    public void Telemetry_trims_whole_frame_rates_to_no_decimals()
    {
        // "0.##" drops trailing zeros, so a whole rate reads "30 fps", not "30.00 fps".
        Assert.Equal("30 fps · 1280×720 · 0:05.0", StatusBarFormat.Telemetry(30.0, 1280, 720, "0:05.0"));
    }
}
