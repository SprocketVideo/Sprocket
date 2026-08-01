using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>The export-metadata default token resolver (PLAN.md step 38): a pure function of its
/// <see cref="MetadataTokenContext"/>, so it is fully unit-tested here.</summary>
public class MetadataTokensTests
{
    private static readonly MetadataTokenContext Ctx =
        new(Username: "Jane", Year: 2026, Project: "My Film", Date: "2026-08-01");

    [Theory]
    [InlineData("{username}", "Jane")]
    [InlineData("{year}", "2026")]
    [InlineData("{project}", "My Film")]
    [InlineData("{date}", "2026-08-01")]
    [InlineData("© {year} {username}", "© 2026 Jane")]
    [InlineData("plain text", "plain text")]
    public void Resolves_Known_Tokens(string template, string expected) =>
        Assert.Equal(expected, MetadataTokens.Resolve(template, Ctx));

    [Theory]
    [InlineData("{USERNAME}")]
    [InlineData("{Year}")]
    [InlineData("{Project}")]
    public void Token_Match_Is_Case_Insensitive(string template) =>
        Assert.Equal(MetadataTokens.Resolve(template.ToLowerInvariant(), Ctx),
                     MetadataTokens.Resolve(template, Ctx));

    [Fact]
    public void Unknown_Token_Is_Left_Verbatim() =>
        Assert.Equal("{director} — Jane", MetadataTokens.Resolve("{director} — {username}", Ctx));

    [Fact]
    public void Empty_Project_Collapses_Surrounding_Whitespace()
    {
        var ctx = Ctx with { Project = "" };
        Assert.Equal("© 2026", MetadataTokens.Resolve("© {year} {project}", ctx));
        Assert.Equal("Jane", MetadataTokens.Resolve("{project} {username}", ctx));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Or_Null_Template_Yields_Empty(string? template) =>
        Assert.Equal("", MetadataTokens.Resolve(template, Ctx));

    [Fact]
    public void Null_Context_Strings_Do_Not_Throw()
    {
        var ctx = new MetadataTokenContext(Username: null!, Year: 2026, Project: null!, Date: null!);
        Assert.Equal("2026", MetadataTokens.Resolve("{username} {year} {project}", ctx));
    }

    [Fact]
    public void Default_Settings_Templates_Resolve_As_Expected()
    {
        var s = new UserSettings();
        Assert.Equal("My Film", MetadataTokens.Resolve(s.ExportTitle, Ctx));
        Assert.Equal("Jane", MetadataTokens.Resolve(s.ExportAuthor, Ctx));
        Assert.Equal("© 2026 Jane", MetadataTokens.Resolve(s.ExportCopyright, Ctx));
        Assert.Equal("", MetadataTokens.Resolve(s.ExportComment, Ctx));
    }
}
