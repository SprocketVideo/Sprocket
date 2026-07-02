using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// The color-metadata probe of PLAN.md step 37: <see cref="MediaSource.Probe"/> reads the codecpar color
/// enums by name and scans the container/stream metadata dictionaries (via <c>av_dict_get</c>) for a DJI
/// log profile. The tagged fixture declares bt709 color metadata and a "D-Log M" container comment.
/// </summary>
public class ColorProbeTests
{
    [Fact]
    public void Probe_Reads_Declared_Color_Metadata_By_Name()
    {
        ProbedMediaInfo info = MediaSource.ProbeInfo(TestVideo.DLogMPath);

        Assert.Equal("tv", info.ColorRange);
        Assert.Equal("bt709", info.ColorPrimaries);
        Assert.Equal("bt709", info.ColorTransfer);
        Assert.Equal("bt709", info.ColorSpace);
        Assert.False(info.IsHdr); // bt709 transfer is SDR — the step-27 flag is unaffected
    }

    [Fact]
    public void Probe_Detects_The_DJI_Log_Profile_From_Container_Metadata()
    {
        ProbedMediaInfo info = MediaSource.ProbeInfo(TestVideo.DLogMPath);
        Assert.Equal(ColorProfiles.DjiDLogM, info.DetectedColorProfile);
    }

    [Fact]
    public void Probe_Leaves_Color_Fields_Absent_On_An_Untagged_Source()
    {
        ProbedMediaInfo info = MediaSource.ProbeInfo(TestVideo.Path);
        Assert.Equal("", info.DetectedColorProfile);
        // The plain fixture declares no primaries/transfer/matrix, so the names read as absent.
        Assert.Equal("", info.ColorPrimaries);
        Assert.Equal("", info.ColorTransfer);
    }
}
