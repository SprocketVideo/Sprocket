using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>The Preferences dialog's pure decision logic (PLAN.md step 38) — the dialog shell itself
/// rests on manual verification per the code-built-dialog policy.</summary>
public class PreferencesFormatTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(-5, "0 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(3_407_872, "3.3 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void Bytes_Formats_Human_Sizes(long bytes, string expected) =>
        Assert.Equal(expected, PreferencesFormat.Bytes(bytes));

    [Fact]
    public void Setup_Command_Includes_Header_Only_When_Token_Required()
    {
        Assert.Equal(
            "claude mcp add --transport http sprocket http://127.0.0.1:41008/mcp",
            PreferencesFormat.McpSetupCommand(41008, requireToken: false, token: "secret"));
        Assert.Equal(
            "claude mcp add --transport http sprocket http://127.0.0.1:5000/mcp --header \"Authorization: Bearer secret\"",
            PreferencesFormat.McpSetupCommand(5000, requireToken: true, token: "secret"));
        // Required but not yet generated: don't emit an empty header.
        Assert.DoesNotContain("--header",
            PreferencesFormat.McpSetupCommand(5000, requireToken: true, token: ""));
    }

    [Theory]
    [InlineData("41008", 41008)]
    [InlineData(" 80 ", 80)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsePort_Accepts_Only_Integers(string? text, int? expected) =>
        Assert.Equal(expected, PreferencesFormat.ParsePort(text));

    [Fact]
    public void ResolveToken_Generates_Once_And_Preserves_On_Disable()
    {
        // First enable with no token → generated.
        string generated = PreferencesFormat.ResolveToken(true, "", () => "fresh");
        Assert.Equal("fresh", generated);

        // Already have one → kept, not regenerated.
        Assert.Equal("existing", PreferencesFormat.ResolveToken(true, "existing", () => "fresh"));

        // Disabled → the existing token is preserved so re-enabling doesn't break configured clients.
        Assert.Equal("existing", PreferencesFormat.ResolveToken(false, "existing", () => "fresh"));
    }
}
