namespace Sprocket.Export;

/// <summary>
/// The <c>SPROCKET_EXPORT_GPU_DECODE</c> opt-in, read once (export-speed phase 3). Set it to <c>1</c> / <c>true</c> /
/// <c>on</c> / <c>yes</c> to make Fast Export decode sources on the GPU (with per-source software fallback, reported in
/// <see cref="ExportRunSummary.Decode"/>). Off by default: on the reference 24-core desktop GPU decode made exports
/// slower in every configuration measured, so it stays a switch for measuring other hardware (laptops, Apple
/// VideoToolbox, Linux VAAPI) rather than a default. Final Export always decodes in software regardless.
/// </summary>
public static class ExportGpuDecode
{
    /// <summary>Whether GPU decode was opted into for Fast Export.</summary>
    public static bool OptedIn { get; } = Read();

    private static bool Read()
    {
        string? v = Environment.GetEnvironmentVariable("SPROCKET_EXPORT_GPU_DECODE");
        return !string.IsNullOrWhiteSpace(v) && v.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }
}
