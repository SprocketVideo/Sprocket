using System.Text.Json;
using System.Text.Json.Serialization;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Export;

/// <summary>
/// Loads and saves the user's custom export presets (PLAN.md step 29) as JSON. The curated built-in delivery presets
/// live in <see cref="ExportCodecs.Presets"/>; this store persists only the user-defined ones so they survive across
/// sessions, and merges the two for the export dialog's dropdown. Persistence is best-effort chrome — a missing or
/// corrupt file yields an empty user list rather than an error, mirroring the app's other small settings stores
/// (<c>WindowStateStore</c>). Serialisation goes through a flat DTO, so the on-disk schema is stable across
/// refactors of the domain records and human-editable (enums by name).
/// </summary>
public static class ExportPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Unset optional fields (no resolution pin, tier-derived rate control, …) stay out of the file entirely,
        // keeping it human-editable; omitted == null on read, so this is schema-compatible both ways.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // A flat, human-editable DTO decoupled from the domain records: no computed-property noise
    // (ExportFormat.IsValid / MuxerName …) and a schema that doesn't move when the records are refactored.
    private sealed record PresetDto(
        string Name,
        ExportContainer Container,
        ExportVideoCodec VideoCodec,
        ExportAudioCodec AudioCodec,
        ExportQuality Quality,
        int? Width,
        int? Height,
        int? FrameRateNum,
        int? FrameRateDen,
        ExportAudioFormat? AudioFormat = null,     // set → audio-only preset (PLAN.md step 44); additive, older files omit it
        ExportRateControl? RateControl = null,     // rate control (additive): null / omitted = quality mode with the
        int? Crf = null,                           //   tier-derived CRF — exactly the pre-rate-control behaviour, so
        long? VideoBitRate = null,                 //   older preset files keep working unchanged
        long? MaxBitRate = null);

    /// <summary>Serialises the presets to the persisted JSON form (exposed for testing).</summary>
    public static string Serialize(IReadOnlyList<ExportPreset> presets) =>
        JsonSerializer.Serialize(presets.Select(ToDto).ToList(), JsonOptions);

    /// <summary>Parses the persisted JSON form back to presets; returns an empty list for null / blank / corrupt
    /// input, and skips any entry with no name.</summary>
    public static IReadOnlyList<ExportPreset> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            List<PresetDto>? dtos = JsonSerializer.Deserialize<List<PresetDto>>(json, JsonOptions);
            if (dtos is null)
                return [];
            var list = new List<ExportPreset>(dtos.Count);
            foreach (PresetDto dto in dtos)
                if (!string.IsNullOrWhiteSpace(dto.Name))
                    list.Add(FromDto(dto));
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads the user presets from <paramref name="path"/>, or an empty list if it is missing / unreadable.</summary>
    public static IReadOnlyList<ExportPreset> Load(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes the user presets to <paramref name="path"/> (creating its folder). Best-effort: IO errors are
    /// swallowed, since losing a preset is harmless chrome.</summary>
    public static void Save(string path, IReadOnlyList<ExportPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serialize(presets));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort persistence of user chrome
        }
    }

    /// <summary>The full dropdown list for the export dialog: the curated built-ins first, then the user's own.</summary>
    public static IReadOnlyList<ExportPreset> BuiltInAndUser(IReadOnlyList<ExportPreset> userPresets) =>
        [.. ExportCodecs.Presets, .. userPresets];

    private static PresetDto ToDto(ExportPreset p) => new(
        p.Name, p.Format.Container, p.Format.VideoCodec, p.Format.AudioCodec, p.Quality,
        p.Resolution?.Width, p.Resolution?.Height, p.FrameRate?.Num, p.FrameRate?.Den, p.AudioFormat,
        // The default-valued rate-control fields serialize as null so a quality-tier preset's JSON is unchanged.
        p.RateControl == ExportRateControl.Quality ? null : p.RateControl,
        p.Crf > 0 ? p.Crf : null,
        p.VideoBitRate > 0 ? p.VideoBitRate : null,
        p.MaxBitRate > 0 ? p.MaxBitRate : null);

    private static ExportPreset FromDto(PresetDto d) => new(
        d.Name,
        new ExportFormat(d.Container, d.VideoCodec, d.AudioCodec),
        d.Quality,
        d.Width is { } w && d.Height is { } h ? new Resolution(w, h) : null,
        d.FrameRateNum is { } n && d.FrameRateDen is { } den && den != 0 ? new Rational(n, den) : null,
        d.AudioFormat,
        d.RateControl ?? ExportRateControl.Quality,
        d.Crf ?? 0,
        d.VideoBitRate ?? 0,
        d.MaxBitRate ?? 0);
}
