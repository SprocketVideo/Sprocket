using System;
using Avalonia;

namespace Sprocket.App;

internal static class Program
{
    /// <summary>
    /// Entry point for the slice's editor app: opens a media file (first CLI arg, or a generated sample
    /// clip), builds a one-video-track project, and plays it in the Skia preview with a transport
    /// (PLAN.md step 4).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
