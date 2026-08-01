using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure, IO-free part of the Linux AppImage desktop integration (PLAN.md step 36): the generated
/// <c>.desktop</c> launcher text. The file/icon writing and cache refresh are best-effort IO and rest on
/// manual verification on Linux; what matters here is that the launcher is a valid freedesktop entry whose
/// <c>Exec</c> resolves to the running AppImage — including when its path contains spaces.
/// </summary>
public class LinuxDesktopIntegrationTests
{
    [Fact]
    public void Builds_A_Valid_Desktop_Entry()
    {
        string entry = LinuxDesktopIntegration.BuildDesktopEntry("/home/me/Apps/Sprocket.AppImage");

        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("\nType=Application\n", entry);
        Assert.Contains("\nName=Sprocket\n", entry);
        Assert.Contains("\nIcon=sprocket\n", entry);
        Assert.Contains("\nCategories=AudioVideo;AudioVideoEditing;Video;\n", entry);
        // StartupWMClass groups the window under this launcher's dock icon.
        Assert.Contains("\nStartupWMClass=Sprocket\n", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void Exec_Points_At_The_AppImage_And_Opens_A_Passed_File()
    {
        string entry = LinuxDesktopIntegration.BuildDesktopEntry("/opt/sprocket/Sprocket.AppImage");

        Assert.Contains("\nExec=\"/opt/sprocket/Sprocket.AppImage\" %f\n", entry);
    }

    [Fact]
    public void Exec_Quotes_A_Path_Containing_Spaces()
    {
        // A path with spaces (a user's home dir often has them) must stay a single Exec argument.
        string entry = LinuxDesktopIntegration.BuildDesktopEntry("/home/Jane Doe/My Apps/Sprocket.AppImage");

        Assert.Contains("Exec=\"/home/Jane Doe/My Apps/Sprocket.AppImage\" %f", entry);
    }

    [Fact]
    public void Exec_Escapes_Reserved_Shell_Characters()
    {
        // The freedesktop spec keeps " ` $ \ meaningful inside quotes, so they must be backslash-escaped.
        string entry = LinuxDesktopIntegration.BuildDesktopEntry("/home/me/$weird/Sprocket.AppImage");

        Assert.Contains("Exec=\"/home/me/\\$weird/Sprocket.AppImage\" %f", entry);
    }
}
