using System.Text.Json;
using System.Text.Json.Serialization;
using Sprocket.Core.Model;

namespace Sprocket.Persistence;

/// <summary>What <see cref="LooksStore.Deserialize"/> recovered: the usable user looks, plus a note for every entry it
/// had to drop or repair (shown as a status hint, never an error).</summary>
public sealed record LooksLoadResult(IReadOnlyList<Look> Looks, IReadOnlyList<string> Warnings);

/// <summary>
/// Loads and saves the user's saved looks (plan/features/looks-browser.md) as JSON — the <c>looks.json</c> beside
/// the export presets, mirroring <c>ExportPresetStore</c>. The curated built-ins live in <see cref="LooksCatalog"/>;
/// this store persists only the user's own and merges the two for the browser (<see cref="BuiltInAndUser"/>).
/// Persistence is best-effort chrome: a missing or corrupt file yields no user looks rather than an error.
/// <para>
/// The file is a flat, human-editable DTO (effect type id → constant values, plus asset paths), so the schema does not
/// move when the domain records are refactored. Reading is defensive because the file is user-editable: a look with no
/// name or no effects is dropped, a missing / duplicate / non-<c>user.</c> id is replaced, and the tier-1 input color
/// transform is stripped (ARCHITECTURE §18 — a look never carries a camera conversion). Effect types that are not
/// registered <em>now</em> are kept verbatim — a plugin may simply not be loaded yet — and skipped when the look is
/// applied (<see cref="LookApplication.Build"/>), so a temporarily missing plugin never erases part of a saved look.
/// </para>
/// </summary>
public static class LooksStore
{
    /// <summary>The current file schema version.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record FileDto(int Version, List<LookDto>? Looks);

    private sealed record LookDto(string? Id, string? Name, string? Description, List<LookEffectDto>? Effects);

    private sealed record LookEffectDto(string? Type, Dictionary<string, double>? Values, Dictionary<string, string>? Assets);

    /// <summary>Serialises the user looks to the persisted JSON form (built-ins are never written).</summary>
    public static string Serialize(IReadOnlyList<Look> looks)
    {
        ArgumentNullException.ThrowIfNull(looks);
        var dto = new FileDto(SchemaVersion, [.. looks.Where(l => !l.IsBuiltIn).Select(ToDto)]);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Parses the persisted JSON form back to user looks, repairing or dropping bad entries (see the type
    /// remarks). Null / blank / corrupt input yields no looks.</summary>
    public static LooksLoadResult Deserialize(string? json)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return new LooksLoadResult([], warnings);

        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"The looks file could not be read ({ex.Message}); no saved looks were loaded.");
            return new LooksLoadResult([], warnings);
        }
        if (file?.Looks is null)
            return new LooksLoadResult([], warnings);

        var looks = new List<Look>(file.Looks.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (LookDto? dto in file.Looks)
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            {
                warnings.Add("Skipped a saved look with no name.");
                continue;
            }
            string name = dto.Name.Trim();

            var entries = new List<LookEntry>();
            foreach (LookEffectDto? e in dto.Effects ?? [])
            {
                if (e is null || string.IsNullOrWhiteSpace(e.Type))
                    continue;
                if (e.Type == EffectTypeIds.ColorTransform)
                {
                    warnings.Add($"Look '{name}': removed the input color transform — looks grade Rec.709 footage and never carry a camera conversion.");
                    continue;
                }
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach ((string key, double value) in e.Values ?? [])
                    if (!string.IsNullOrEmpty(key) && double.IsFinite(value))
                        values[key] = value;
                Dictionary<string, string>? assets = null;
                foreach ((string key, string path) in e.Assets ?? [])
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(path))
                        (assets ??= new(StringComparer.Ordinal))[key] = path;
                entries.Add(new LookEntry(e.Type, values, assets));
            }
            if (entries.Count == 0)
            {
                warnings.Add($"Skipped look '{name}': it has no effects.");
                continue;
            }

            string id = dto.Id is { } candidate && candidate.StartsWith(Look.UserPrefix, StringComparison.Ordinal)
                && candidate.Length > Look.UserPrefix.Length && !ids.Contains(candidate)
                ? candidate
                : Look.NewUserId();
            ids.Add(id);
            string? description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            looks.Add(new Look(id, name, Look.UserGroup, description, entries));
        }
        return new LooksLoadResult(looks, warnings);
    }

    /// <summary>Reads the user looks from <paramref name="path"/>; a missing or unreadable file yields none.</summary>
    public static LooksLoadResult Load(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new LooksLoadResult([], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LooksLoadResult([], [$"The looks file could not be opened ({ex.Message})."]);
        }
    }

    /// <summary>
    /// Writes the user looks to <paramref name="path"/> (creating its folder). The file is written beside the target
    /// and then moved over it, so a crash or full disk mid-write never truncates the looks the user already has.
    /// Returns whether the save succeeded; IO errors are swallowed (best-effort chrome).
    /// </summary>
    public static bool Save(string path, IReadOnlyList<Look> looks)
    {
        string temp = $"{path}.{Environment.ProcessId}.tmp"; // per process, so two running instances never share one
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, Serialize(looks));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>The Looks browser's full list: the curated built-ins first, then the user's own.</summary>
    public static IReadOnlyList<Look> BuiltInAndUser(IReadOnlyList<Look> userLooks) =>
        [.. LooksCatalog.BuiltIns, .. userLooks];

    private static LookDto ToDto(Look look) => new(
        look.Id,
        look.Name,
        look.Description,
        [.. look.Entries.Select(e => new LookEffectDto(
            e.EffectTypeId,
            new Dictionary<string, double>(e.Values),
            e.Assets is { Count: > 0 } a ? new Dictionary<string, string>(a) : null))]);
}
