using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Headless tests for the export-preset model and its JSON store (PLAN.md step 29): a preset carries the step-27
/// matrix plus an optional resolution / frame-rate, maps to <see cref="ExportOptions"/>, and round-trips through the
/// persisted form. No FFmpeg / Skia — the actual encode of an overridden resolution / rate is exercised by the
/// real-encode tests.
/// </summary>
public sealed class ExportPresetTests
{
    private static readonly ExportPreset FormatOnly =
        new("MP4 · H.264 + AAC", new ExportFormat(ExportContainer.Mp4, ExportVideoCodec.H264, ExportAudioCodec.Aac), ExportQuality.High);

    private static readonly ExportPreset WithResAndRate =
        new("YouTube 1080p59.94",
            new ExportFormat(ExportContainer.Mp4, ExportVideoCodec.Hevc, ExportAudioCodec.Aac),
            ExportQuality.Medium,
            new Resolution(1920, 1080),
            new Rational(60000, 1001));

    [Fact]
    public void ToOptions_CarriesFormatQualityResolutionAndFrameRate()
    {
        ExportOptions options = WithResAndRate.ToOptions();

        Assert.Equal(WithResAndRate.Format, options.Format);
        Assert.Equal(ExportQuality.Medium, options.Quality);
        Assert.Equal(new Resolution(1920, 1080), options.Resolution);
        Assert.Equal(new Rational(60000, 1001), options.FrameRate);
    }

    [Fact]
    public void ToOptions_LeavesResolutionAndFrameRateNull_ForAFormatOnlyPreset()
    {
        ExportOptions options = FormatOnly.ToOptions();

        Assert.Null(options.Resolution);
        Assert.Null(options.FrameRate);
    }

    [Fact]
    public void Store_RoundTripsPresetsThroughJson_PreservingOverrides()
    {
        IReadOnlyList<ExportPreset> original = [FormatOnly, WithResAndRate];

        string json = ExportPresetStore.Serialize(original);
        IReadOnlyList<ExportPreset> restored = ExportPresetStore.Deserialize(json);

        Assert.Equal(original, restored);
        // The format-only preset keeps null overrides; the other keeps its resolution / rate.
        Assert.Null(restored[0].Resolution);
        Assert.Null(restored[0].FrameRate);
        Assert.Equal(new Resolution(1920, 1080), restored[1].Resolution);
        Assert.Equal(new Rational(60000, 1001), restored[1].FrameRate);
    }

    [Fact]
    public void Store_SerializesEnumsByName_ForAStableHumanEditableFile()
    {
        string json = ExportPresetStore.Serialize([WithResAndRate]);

        // Names, not ordinals, so reordering the enums later can't silently repoint a saved preset.
        Assert.Contains("Mp4", json);
        Assert.Contains("Hevc", json);
        Assert.Contains("Aac", json);
        Assert.Contains("Medium", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]              // a JSON object, not the expected array
    public void Store_Deserialize_ReturnsEmpty_ForBlankOrCorruptInput(string? json)
    {
        Assert.Empty(ExportPresetStore.Deserialize(json));
    }

    [Fact]
    public void Store_Deserialize_SkipsEntriesWithNoName()
    {
        // A hand-edited file with a nameless entry: it is dropped rather than surfaced as a blank dropdown row.
        string json = """
        [
          { "Name": "Good", "Container": "Mp4", "VideoCodec": "H264", "AudioCodec": "Aac", "Quality": "High" },
          { "Name": "",     "Container": "Mp4", "VideoCodec": "H264", "AudioCodec": "Aac", "Quality": "High" }
        ]
        """;

        IReadOnlyList<ExportPreset> presets = ExportPresetStore.Deserialize(json);
        Assert.Single(presets);
        Assert.Equal("Good", presets[0].Name);
    }

    [Fact]
    public void Store_LoadSave_RoundTripsThroughAFile_AndLoadIsEmptyForAMissingPath()
    {
        using var file = new TempFile(".json");

        // Missing file → empty (best-effort chrome, never throws).
        Assert.Empty(ExportPresetStore.Load(file.Path));

        IReadOnlyList<ExportPreset> presets = [FormatOnly, WithResAndRate];
        ExportPresetStore.Save(file.Path, presets);

        Assert.True(File.Exists(file.Path));
        Assert.Equal(presets, ExportPresetStore.Load(file.Path));
    }

    [Fact]
    public void Store_BuiltInAndUser_ListsTheBuiltInsFirstThenTheUsersOwn()
    {
        IReadOnlyList<ExportPreset> user = [new("My Preset", default, ExportQuality.Low)];

        IReadOnlyList<ExportPreset> merged = ExportPresetStore.BuiltInAndUser(user);

        Assert.Equal(ExportCodecs.Presets.Count + 1, merged.Count);
        Assert.Equal(ExportCodecs.Presets[0].Name, merged[0].Name);   // built-ins lead
        Assert.Equal("My Preset", merged[^1].Name);                    // user preset trails
    }

    [Fact]
    public void BuiltInPresets_IncludeResolutionPinnedDeliveryPresets()
    {
        // The curated list carries both format-only presets and at least one that pins a delivery resolution
        // (the "YouTube 1080p / 4K" style presets the leading NLEs ship).
        Assert.Contains(ExportCodecs.Presets, p => p.Resolution is null);
        Assert.Contains(ExportCodecs.Presets, p => p.Resolution is { Width: 1920, Height: 1080 });
    }
}
