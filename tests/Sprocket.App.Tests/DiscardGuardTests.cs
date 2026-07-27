using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The close/quit guard's pure decision + prompt text (the dialogs themselves rest on manual verification,
/// like the shell's other code-built dialogs).
/// </summary>
public class DiscardGuardTests
{
    [Fact]
    public void IdleAndCleanSessionIsNeverPrompted()
    {
        Assert.False(DiscardGuard.NeedsPrompt(exporting: false, dirty: false, alreadyApproved: false));
        Assert.False(DiscardGuard.NeedsPrompt(exporting: false, dirty: false, alreadyApproved: true));
    }

    [Fact]
    public void UnsavedEditsArePrompted()
    {
        Assert.True(DiscardGuard.NeedsPrompt(exporting: false, dirty: true, alreadyApproved: false));
    }

    /// <summary>A saved project is still worth stopping for while an export is writing a file — closing would
    /// leave the partly-written output behind.</summary>
    [Fact]
    public void RunningExportIsPromptedEvenWhenSaved()
    {
        Assert.True(DiscardGuard.NeedsPrompt(exporting: true, dirty: false, alreadyApproved: false));
    }

    [Fact]
    public void BothStakesAtOnceArePrompted()
    {
        Assert.True(DiscardGuard.NeedsPrompt(exporting: true, dirty: true, alreadyApproved: false));
    }

    /// <summary>The second pass must not re-ask: the close / quit gates cancel the first attempt, prompt, then
    /// re-issue the very same close — and a session swap closes a window already answered for.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApprovedCloseIsNotPromptedAgain(bool exporting, bool dirty)
    {
        Assert.False(DiscardGuard.NeedsPrompt(exporting, dirty, alreadyApproved: true));
    }

    [Fact]
    public void UnsavedMessageNamesTheDocumentAndStatesWhatIsLost()
    {
        string message = DiscardGuard.UnsavedChangesMessage("Wedding Cut");

        Assert.Contains("Wedding Cut", message);
        Assert.Contains("save the changes", message);
        Assert.Contains("lost", message);
    }

    /// <summary>The export prompt has to say both halves: the partial file goes, finished exports do not.</summary>
    [Fact]
    public void ExportMessageCoversWhatIsLostAndWhatIsNot()
    {
        Assert.Contains("cancels it", DiscardGuard.RunningExportMessage);
        Assert.Contains("partly-written", DiscardGuard.RunningExportMessage);
        Assert.Contains("already finished are not affected", DiscardGuard.RunningExportMessage);
    }

    /// <summary>Cancel must be the enum's default: <c>ShowDialog&lt;T&gt;</c> returns <c>default</c> when the
    /// prompt is dismissed by its title-bar close button, and that gesture must not discard or save anything.</summary>
    [Fact]
    public void DismissingTheSavePromptDefaultsToCancel()
    {
        Assert.Equal(SaveChangesChoice.Cancel, default(SaveChangesChoice));
    }
}
