using System;
using System.Collections.Generic;
using System.IO;
using Sprocket.Export;

namespace Sprocket.App;

/// <summary>
/// App-level wrapper that persists the user's custom export presets (PLAN.md step 29) to a small JSON file under the
/// per-platform application-data folder, mirroring <see cref="WindowStateStore"/>. The (de)serialisation, merge with
/// the built-ins, and error handling live in <see cref="ExportPresetStore"/> (headlessly tested); this only owns the
/// file location.
/// </summary>
internal static class UserExportPresets
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sprocket", "export-presets.json");

    /// <summary>The user's persisted custom presets (empty if none / unreadable).</summary>
    public static IReadOnlyList<ExportPreset> Load() => ExportPresetStore.Load(FilePath);

    /// <summary>Persists the user's custom presets (best-effort).</summary>
    public static void Save(IReadOnlyList<ExportPreset> presets) => ExportPresetStore.Save(FilePath, presets);
}
