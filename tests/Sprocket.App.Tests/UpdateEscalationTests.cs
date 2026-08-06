using System;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure logic behind the refined (non-intrusive) update notification (PLAN.md steps 36 + 45): the
/// age-based badge escalation and the once-per-version first-run-toast gate. The UI wiring in
/// <c>MainWindow.RefreshUpdateAffordances</c> is a thin shell over these and rests on manual verification.
/// </summary>
public class UpdateEscalationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // Expected passed as int (UpdateTier is internal — a public [Theory] signature can't name it): 0=Fresh,
    // 1=Aging, 2=Stale, matching the enum's declaration order.
    [Theory]
    [InlineData(0, (int)UpdateTier.Fresh)]
    [InlineData(1, (int)UpdateTier.Fresh)]
    [InlineData(1.99, (int)UpdateTier.Fresh)]
    [InlineData(2, (int)UpdateTier.Aging)]
    [InlineData(6, (int)UpdateTier.Aging)]
    [InlineData(6.99, (int)UpdateTier.Aging)]
    [InlineData(7, (int)UpdateTier.Stale)]
    [InlineData(30, (int)UpdateTier.Stale)]
    public void TierFor_Escalates_With_Age(double daysAgo, int expected)
    {
        DateTimeOffset firstSeen = Now.AddDays(-daysAgo);
        Assert.Equal((UpdateTier)expected, UpdateEscalation.TierFor(firstSeen, Now));
    }

    [Fact]
    public void TierFor_Future_FirstSeen_Clamps_To_Fresh()
    {
        // Clock skew: a first-seen stamp in the future must not throw or over-escalate.
        Assert.Equal(UpdateTier.Fresh, UpdateEscalation.TierFor(Now.AddDays(5), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-timestamp")]
    public void TierFor_Missing_Or_Garbage_Timestamp_Is_Fresh(string? stamp) =>
        Assert.Equal(UpdateTier.Fresh, UpdateEscalation.TierFor(stamp, Now));

    [Fact]
    public void TierFor_Roundtrips_Through_Format()
    {
        string stamp = UpdateEscalation.Format(Now.AddDays(-8));
        Assert.Equal(UpdateTier.Stale, UpdateEscalation.TierFor(stamp, Now));
    }

    [Fact]
    public void ShouldToast_Fires_Once_For_A_New_Version()
    {
        Assert.True(UpdateEscalation.ShouldToast("0.2.0", toastShownTag: "", dismissedTag: "", isInstalled: true));
    }

    [Fact]
    public void ShouldToast_Suppressed_After_Shown_For_That_Version()
    {
        Assert.False(UpdateEscalation.ShouldToast("0.2.0", toastShownTag: "0.2.0", dismissedTag: "", isInstalled: true));
    }

    [Fact]
    public void ShouldToast_Refires_For_A_Newer_Version()
    {
        Assert.True(UpdateEscalation.ShouldToast("0.2.1", toastShownTag: "0.2.0", dismissedTag: "", isInstalled: true));
    }

    [Fact]
    public void ShouldToast_Suppressed_For_Skipped_Version()
    {
        Assert.False(UpdateEscalation.ShouldToast("0.2.0", toastShownTag: "", dismissedTag: "0.2.0", isInstalled: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldToast_False_When_No_Version(string? version) =>
        Assert.False(UpdateEscalation.ShouldToast(version, toastShownTag: "", dismissedTag: "", isInstalled: true));

    [Fact]
    public void ShouldToast_Never_For_Portable_Build()
    {
        // Portable/dev builds can't self-update — no toast, no badge, no dot.
        Assert.False(UpdateEscalation.ShouldToast("0.2.0", toastShownTag: "", dismissedTag: "", isInstalled: false));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SoftenMarkdown_Empty_Input_Is_Empty(string? input, string expected) =>
        Assert.Equal(expected, UpdateAvailableDialog.SoftenMarkdown(input));

    [Fact]
    public void SoftenMarkdown_Strips_Headings_Bullets_And_Emphasis()
    {
        string md = "# What's New\r\n\r\n- **Fixed** a `crash`\r\n* Added export\r\n";
        string text = UpdateAvailableDialog.SoftenMarkdown(md);
        Assert.Equal("What's New\n\n• Fixed a crash\n• Added export", text);
    }

    // changelog.ps1's summary line is whole-line italics; a mid-line underscore is an identifier.
    [Fact]
    public void SoftenMarkdown_Strips_Whole_Line_Italics_Only()
    {
        string md = "_3 commits in this release._\r\n\r\n- renamed source_in to SourceIn\r\n";
        string text = UpdateAvailableDialog.SoftenMarkdown(md);
        Assert.Equal("3 commits in this release.\n\n• renamed source_in to SourceIn", text);
    }

    // Defensive: a hand-authored HTML comment (GitHub hides them) must not render verbatim in the
    // "what's new" box, where it would read like the prompt used to generate the notes.
    [Fact]
    public void SoftenMarkdown_Strips_Html_Comments()
    {
        string md = "<!--\r\n  Authoring guidance: keep this version-agnostic.\r\n-->\r\n\r\n# Sprocket\r\n\r\n- A change\r\n";
        string text = UpdateAvailableDialog.SoftenMarkdown(md);
        Assert.Equal("Sprocket\n\n• A change", text);
    }

    // "Full Release Notes" must land on the release the user was just offered — the releases index
    // doesn't show that version's change overview.
    [Fact]
    public void ReleaseUrlFor_Deep_Links_To_The_Offered_Version() =>
        Assert.Equal(
            UpdateService.RepoUrl + "/releases/tag/v0.1.89-alpha",
            UpdateService.ReleaseUrlFor("0.1.89-alpha"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReleaseUrlFor_Falls_Back_To_The_Releases_Index(string? version) =>
        Assert.Equal(UpdateService.ReleasesPageUrl, UpdateService.ReleaseUrlFor(version));
}
