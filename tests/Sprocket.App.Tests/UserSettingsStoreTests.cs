using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The user-scoped settings store of PLAN.md step 38: round-trip fidelity, tolerance of
/// missing/garbage/partial files, clamping, and bearer-token generation. The file-location wrapper
/// (<c>UserSettingsFile</c>) is a trivial IO shim and rests on manual verification.
/// </summary>
public class UserSettingsStoreTests
{
    [Fact]
    public void Round_Trips_All_Fields()
    {
        var settings = new UserSettings(
            ExportTitle: "My Film", ExportAuthor: "Jane", ExportCopyright: "© 2026", ExportComment: "cut 3",
            AutosaveIntervalSeconds: 30, McpEnabled: true, McpPort: 5555, McpRequireToken: true, McpToken: "abc123");
        Assert.Equal(settings, UserSettingsStore.Deserialize(UserSettingsStore.Serialize(settings)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {")]
    [InlineData("[1, 2, 3]")]
    public void Missing_Or_Garbage_Input_Yields_Defaults(string? json) =>
        Assert.Equal(new UserSettings(), UserSettingsStore.Deserialize(json));

    [Fact]
    public void Partial_Json_Keeps_Defaults_For_Absent_Fields()
    {
        UserSettings s = UserSettingsStore.Deserialize("""{"McpEnabled": true, "ExportAuthor": "Jane"}""");
        Assert.True(s.McpEnabled);
        Assert.Equal("Jane", s.ExportAuthor);
        Assert.Equal(UserSettingsStore.DefaultMcpPort, s.McpPort);
        Assert.Equal(5, s.AutosaveIntervalSeconds);
        Assert.False(s.McpRequireToken);
    }

    [Fact]
    public void Unknown_Fields_Are_Ignored() =>
        Assert.Equal(new UserSettings(),
            UserSettingsStore.Deserialize("""{"SomeFutureSetting": 42}"""));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10_000, 600)]
    [InlineData(60, 60)]
    public void Clamp_Bounds_The_Autosave_Interval(int stored, int expected) =>
        Assert.Equal(expected, UserSettingsStore.Clamp(new UserSettings(AutosaveIntervalSeconds: stored)).AutosaveIntervalSeconds);

    [Theory]
    [InlineData(80, 1024)]
    [InlineData(0, 1024)]
    [InlineData(70_000, 65535)]
    [InlineData(41008, 41008)]
    public void Clamp_Bounds_The_Mcp_Port(int stored, int expected) =>
        Assert.Equal(expected, UserSettingsStore.Clamp(new UserSettings(McpPort: stored)).McpPort);

    [Fact]
    public void Clamp_Normalizes_Null_Strings()
    {
        UserSettings s = UserSettingsStore.Clamp(new UserSettings(ExportTitle: null!, McpToken: null!));
        Assert.Equal("", s.ExportTitle);
        Assert.Equal("", s.McpToken);
    }

    [Fact]
    public void Deserialize_Clamps_Hand_Edited_Values()
    {
        UserSettings s = UserSettingsStore.Deserialize("""{"McpPort": 1, "AutosaveIntervalSeconds": 99999}""");
        Assert.Equal(1024, s.McpPort);
        Assert.Equal(600, s.AutosaveIntervalSeconds);
    }

    [Fact]
    public void NewToken_Is_Long_Unique_And_Header_Safe()
    {
        string a = UserSettingsStore.NewToken();
        string b = UserSettingsStore.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes → 43 base64url chars, unpadded
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }
}
