namespace Sprocket.Core.Commands;

/// <summary>
/// A single reversible edit to the project model (ARCHITECTURE.md §4). Every model mutation is meant to
/// be expressed as one of these and run through the <see cref="EditHistory"/>, so undo/redo, command
/// merging (e.g. a slider drag), and an edit-history surface come for free as the editor grows. Commands
/// are cheap to record and reverse because the model is plain data with no native handles.
/// </summary>
public interface IEditCommand
{
    /// <summary>A short, human-readable description for the edit-history surface (e.g. "Move clip").</summary>
    string Label { get; }

    /// <summary>Applies the edit. Called once when the command is executed and again on redo.</summary>
    void Apply();

    /// <summary>Reverses the edit, restoring the state captured before <see cref="Apply"/>.</summary>
    void Revert();

    /// <summary>
    /// Attempts to absorb <paramref name="next"/> into this command so a continuous gesture (a slider drag,
    /// a clip being dragged) collapses into one undo entry. Returns <see langword="true"/> if merged — in
    /// which case <paramref name="next"/> has already been applied and must not be pushed separately. The
    /// default is to never merge; see <see cref="EditCommand"/>.
    /// </summary>
    bool TryMergeWith(IEditCommand next);
}

/// <summary>
/// Convenience base class for <see cref="IEditCommand"/>s: carries the <see cref="Label"/> and opts out of
/// merging by default. Commands that support coalescing override <see cref="TryMergeWith"/>.
/// </summary>
public abstract class EditCommand(string label) : IEditCommand
{
    /// <inheritdoc />
    public string Label { get; } = label;

    /// <inheritdoc />
    public abstract void Apply();

    /// <inheritdoc />
    public abstract void Revert();

    /// <inheritdoc />
    public virtual bool TryMergeWith(IEditCommand next) => false;
}
