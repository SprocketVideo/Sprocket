namespace Sprocket.App;

/// <summary>
/// The pure parts of the guard that stands in front of anything which would throw work away — File ▸ New /
/// Open / Open Sample, closing the shell window, and quitting. Two different things can be at stake, and the
/// prompts are asked in that order: an export still running (its partly-written file is the work at risk),
/// then edits that have never reached disk.
///
/// <para>The dialogs themselves are <see cref="ConfirmDialog"/> / <see cref="SaveChangesDialog"/> and the
/// wiring lives in <c>MainWindow</c> / <c>App</c>; the decision and the prompt text live here so both are
/// testable without a UI thread (the same split the other code-built dialogs use — see the header of
/// <c>Dialogs.cs</c>).</para>
/// </summary>
internal static class DiscardGuard
{
    /// <summary>
    /// Whether closing or quitting has to stop and ask. Either stake on its own is enough.
    /// <paramref name="alreadyApproved"/> is the escape hatch for the second pass: both the window-close and
    /// the quit paths cancel the first attempt, ask, and then re-issue it — and a session swap closes the
    /// outgoing window over a document the user already answered for. Neither may prompt a second time.
    /// </summary>
    internal static bool NeedsPrompt(bool exporting, bool dirty, bool alreadyApproved) =>
        !alreadyApproved && (exporting || dirty);

    /// <summary>
    /// The unsaved-changes prompt body, naming the document. Follows the phrasing every desktop platform's own
    /// save-on-close alert uses, so the wording is already familiar: the question first, then what is at stake
    /// if the answer is "Don't Save".
    /// </summary>
    internal static string UnsavedChangesMessage(string documentName) =>
        $"Do you want to save the changes you made to “{documentName}”?"
        + "\n\nYour changes will be lost if you don't save them.";

    /// <summary>The running-export prompt body. Names the one thing that is actually lost (the file being
    /// written) and, just as important, the thing that is not — exports that already finished.</summary>
    internal const string RunningExportMessage =
        "An export is still running. Closing now cancels it and deletes the partly-written file.\n\n"
        + "Exports that have already finished are not affected.";
}
