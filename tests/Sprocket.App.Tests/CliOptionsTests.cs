using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The launch command line of the PLAN.md step 38 follow-on: the scripting flags (<c>--mcp</c> /
/// <c>--mcp-port</c>) that start the loopback MCP server for one session, media-path extraction, and
/// the session-only overlay of those flags (plus the env-var token) onto the persisted settings.
/// </summary>
public class CliOptionsTests
{
    [Fact]
    public void Empty_Args_Yield_Defaults()
    {
        CliOptions cli = CliOptions.Parse([]);
        Assert.Equal(new CliOptions(), cli);
    }

    [Fact]
    public void Bare_Argument_Is_The_Media_Path()
    {
        CliOptions cli = CliOptions.Parse([@"C:\clips\a.mp4"]);
        Assert.Equal(@"C:\clips\a.mp4", cli.MediaPath);
        Assert.False(cli.McpRequested);
    }

    [Fact]
    public void First_Bare_Argument_Wins_As_Media_Path() =>
        Assert.Equal("a.mp4", CliOptions.Parse(["a.mp4", "b.mp4"]).MediaPath);

    [Fact]
    public void Mcp_Flag_Requests_Server_Without_Port_Override()
    {
        CliOptions cli = CliOptions.Parse(["--mcp"]);
        Assert.True(cli.McpRequested);
        Assert.Null(cli.McpPort);
        Assert.Null(cli.Error);
    }

    [Fact]
    public void Mcp_Port_Implies_Mcp_And_Overrides_Port()
    {
        CliOptions cli = CliOptions.Parse(["--mcp-port", "45000"]);
        Assert.True(cli.McpRequested);
        Assert.Equal(45000, cli.McpPort);
        Assert.Null(cli.Error);
    }

    [Fact]
    public void Flags_And_Media_Path_Combine_In_Any_Order()
    {
        CliOptions cli = CliOptions.Parse(["--mcp-port", "45000", "clip.mp4"]);
        Assert.Equal("clip.mp4", cli.MediaPath);
        Assert.Equal(45000, cli.McpPort);

        cli = CliOptions.Parse(["clip.mp4", "--mcp"]);
        Assert.Equal("clip.mp4", cli.MediaPath);
        Assert.True(cli.McpRequested);
    }

    [Fact]
    public void Port_Value_Is_Consumed_Not_Mistaken_For_Media_Path() =>
        Assert.Null(CliOptions.Parse(["--mcp-port", "45000"]).MediaPath);

    [Theory]
    [InlineData("--mcp-port")]         // missing value
    [InlineData("--mcp-port x")]       // not a number
    [InlineData("--mcp-port 80")]      // below the non-privileged range
    [InlineData("--mcp-port 70000")]   // above the TCP range
    public void Bad_Port_Reports_Error_And_Requests_Nothing(string commandLine)
    {
        CliOptions cli = CliOptions.Parse(commandLine.Split(' '));
        Assert.NotNull(cli.Error);
        Assert.False(cli.McpRequested);
        Assert.Null(cli.McpPort);
    }

    [Fact]
    public void Unknown_Flags_Are_Ignored_Not_Treated_As_Media_Path()
    {
        CliOptions cli = CliOptions.Parse(["--future-flag", "clip.mp4", "--mcp"]);
        Assert.Equal("clip.mp4", cli.MediaPath);
        Assert.True(cli.McpRequested);
        Assert.Null(cli.Error);
    }

    [Fact]
    public void Overlay_Without_Flags_Returns_Persisted_Settings_Unchanged()
    {
        var persisted = new UserSettings(McpEnabled: false, McpPort: 42000);
        Assert.Equal(persisted, CliOptions.Parse([]).ApplyTo(persisted));
    }

    [Fact]
    public void Overlay_Enables_Mcp_And_Keeps_Persisted_Port_Without_Override()
    {
        var persisted = new UserSettings(McpEnabled: false, McpPort: 42000);
        UserSettings effective = CliOptions.Parse(["--mcp"]).ApplyTo(persisted);
        Assert.True(effective.McpEnabled);
        Assert.Equal(42000, effective.McpPort);
    }

    [Fact]
    public void Overlay_Port_Override_Wins_For_The_Session()
    {
        var persisted = new UserSettings(McpEnabled: false, McpPort: 42000);
        UserSettings effective = CliOptions.Parse(["--mcp-port", "45000"]).ApplyTo(persisted);
        Assert.True(effective.McpEnabled);
        Assert.Equal(45000, effective.McpPort);
    }

    [Fact]
    public void Overlay_Env_Token_Requires_And_Sets_The_Token()
    {
        var persisted = new UserSettings(McpRequireToken: false, McpToken: "");
        UserSettings effective = CliOptions.Parse(["--mcp"]).ApplyTo(persisted, envToken: "secret");
        Assert.True(effective.McpRequireToken);
        Assert.Equal("secret", effective.McpToken);
    }

    [Fact]
    public void Overlay_Env_Token_Applies_Even_Without_Mcp_Flag()
    {
        // The token strengthens auth for the persisted-toggle path too — e.g. a wrapper script that
        // exports SPROCKET_MCP_TOKEN while the user left the Preferences toggle on.
        var persisted = new UserSettings(McpEnabled: true, McpRequireToken: false);
        UserSettings effective = CliOptions.Parse([]).ApplyTo(persisted, envToken: "secret");
        Assert.True(effective.McpRequireToken);
        Assert.Equal("secret", effective.McpToken);
    }

    [Fact]
    public void Overlay_With_Parse_Error_Does_Not_Enable_Mcp()
    {
        var persisted = new UserSettings(McpEnabled: false);
        Assert.False(CliOptions.Parse(["--mcp-port", "x"]).ApplyTo(persisted).McpEnabled);
    }
}
