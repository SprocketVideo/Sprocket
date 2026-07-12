using Sprocket.App;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests the pure sequence-format preset table behind the New Sequence / Sequence Settings dialog: the
/// preset↔resolution mapping (unmatched sizes land on Custom) and the custom width/height validation.
/// </summary>
public class SequenceFormatPresetsTests
{
    [Fact]
    public void Presets_End_With_Custom_And_Include_Portrait_Formats()
    {
        Assert.Null(SequenceFormatPresets.Presets[SequenceFormatPresets.CustomIndex].Value);
        Assert.Contains(SequenceFormatPresets.Presets, p => p.Value == new Resolution(1080, 1920));
        Assert.Contains(SequenceFormatPresets.Presets, p => p.Value == new Resolution(1080, 1350));
        Assert.Contains(SequenceFormatPresets.Presets, p => p.Value == new Resolution(1080, 1080));
        Assert.Contains(SequenceFormatPresets.Presets, p => p.Value == new Resolution(2160, 3840));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(3840, 2160)]
    public void IndexOf_Finds_Each_Preset(int width, int height)
    {
        int index = SequenceFormatPresets.IndexOf(new Resolution(width, height));
        Assert.NotEqual(SequenceFormatPresets.CustomIndex, index);
        Assert.Equal(new Resolution(width, height), SequenceFormatPresets.Presets[index].Value);
    }

    [Fact]
    public void IndexOf_Falls_Back_To_Custom_For_An_Unlisted_Size()
    {
        Assert.Equal(SequenceFormatPresets.CustomIndex, SequenceFormatPresets.IndexOf(new Resolution(1234, 700)));
    }

    [Theory]
    [InlineData("1920", "1080", true, 1920, 1080)]
    [InlineData(" 1080 ", " 1920 ", true, 1080, 1920)] // whitespace tolerated
    [InlineData("16", "16", true, 16, 16)]              // lower bound inclusive
    [InlineData("8192", "8192", true, 8192, 8192)]      // upper bound inclusive
    [InlineData("15", "1080", false, 0, 0)]             // below minimum
    [InlineData("8193", "1080", false, 0, 0)]           // above maximum
    [InlineData("", "1080", false, 0, 0)]
    [InlineData("abc", "1080", false, 0, 0)]
    [InlineData("1920.5", "1080", false, 0, 0)]
    [InlineData("-1920", "1080", false, 0, 0)]
    public void TryParse_Validates_Both_Dimensions(string w, string h, bool ok, int width, int height)
    {
        Assert.Equal(ok, SequenceFormatPresets.TryParse(w, h, out Resolution resolution));
        if (ok)
            Assert.Equal(new Resolution(width, height), resolution);
    }
}
