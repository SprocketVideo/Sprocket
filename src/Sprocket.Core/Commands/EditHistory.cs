namespace Sprocket.Core.Commands;

/// <summary>
/// The command stack at the heart of undo/redo (ARCHITECTURE.md §4, PLAN.md step 10). Editing features run
/// every model mutation through <see cref="Execute"/>; this class records it, supports <see cref="Undo"/>/
/// <see cref="Redo"/>, coalesces continuous gestures into single entries, and exposes the stack labels so the
/// UI can show an edit history. Doing this first means later editing features are undoable by construction.
/// </summary>
/// <remarks>
/// Not thread-safe: all editing happens on the UI thread (ARCHITECTURE.md §8 — the UI thread owns the model;
/// decode/render/audio threads only read it). A fresh redo stack is discarded the moment a new edit is
/// executed, matching the universal linear-undo expectation.
/// </remarks>
public sealed class EditHistory
{
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];
    private int _coalescingDepth;
    private EditTransaction? _transaction;

    /// <summary>Raised after any change to the stacks (execute / undo / redo / clear), for UI binding.</summary>
    public event Action? Changed;

    /// <summary>Whether there is an edit to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is an undone edit to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The number of edits on the undo stack. A stable marker for dirty-state tracking — record it on
    /// save and compare: the document is clean exactly while the count matches the saved marker.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>The number of undone edits available to redo.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>The label of the next edit <see cref="Undo"/> would reverse, or <see langword="null"/>.</summary>
    public string? UndoLabel => CanUndo ? _undo[^1].Label : null;

    /// <summary>The label of the next edit <see cref="Redo"/> would re-apply, or <see langword="null"/>.</summary>
    public string? RedoLabel => CanRedo ? _redo[^1].Label : null;

    /// <summary>The undo stack's labels, oldest → newest, for an edit-history surface.</summary>
    public IReadOnlyList<string> UndoLabels => _undo.Select(c => c.Label).ToList();

    /// <summary>The redo stack's labels, newest → oldest (i.e. next-to-redo first).</summary>
    public IReadOnlyList<string> RedoLabels => Enumerable.Reverse(_redo).Select(c => c.Label).ToList();

    /// <summary>
    /// Applies <paramref name="command"/> and records it for undo, clearing the redo stack. Within an active
    /// <see cref="BeginCoalescing"/> scope, if the previous command can absorb this one
    /// (<see cref="IEditCommand.TryMergeWith"/>) the two collapse into a single undo entry.
    /// </summary>
    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Apply();
        _redo.Clear();

        if (_transaction is { } transaction)
        {
            // An open transaction collects the applied command; the whole group lands as one undo
            // entry when it commits (see BeginTransaction).
            transaction.Absorb(command);
        }
        else if (_coalescingDepth > 0 && _undo.Count > 0 && _undo[^1].TryMergeWith(command))
        {
            // Absorbed into the previous entry — the gesture stays one undo step.
        }
        else
        {
            _undo.Add(command);
        }

        Changed?.Invoke();
    }

    /// <summary>Reverses the most recent edit. Returns <see langword="false"/> if there is nothing to undo.
    /// An open transaction is committed first, so the undo reverses the whole group.</summary>
    public bool Undo()
    {
        _transaction?.Commit();
        if (!CanUndo)
            return false;

        IEditCommand command = Pop(_undo);
        command.Revert();
        _redo.Add(command);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Re-applies the most recently undone edit. Returns <see langword="false"/> if there is none.</summary>
    public bool Redo()
    {
        _transaction?.Commit();
        if (!CanRedo)
            return false;

        IEditCommand command = Pop(_redo);
        command.Apply();
        _undo.Add(command);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Clears both stacks (e.g. when a new project is loaded). An open transaction is discarded
    /// without reverting (its edits belong to the document state being torn down).</summary>
    public void Clear()
    {
        _transaction?.Discard();
        if (_undo.Count == 0 && _redo.Count == 0)
            return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Whether a <see cref="BeginTransaction"/> group is currently open.</summary>
    public bool HasOpenTransaction => _transaction is not null;

    /// <summary>
    /// Opens a transaction: every <see cref="Execute"/> until <see cref="EditTransaction.Commit"/> is collected
    /// into a single undo entry labelled <paramref name="label"/> (an already-applied
    /// <see cref="CompositeCommand"/>), so a multi-command edit spanning several calls undoes as one step.
    /// <see cref="EditTransaction.Cancel"/> reverts the collected commands instead. <see cref="Undo"/>/<see cref="Redo"/>
    /// while a transaction is open commit it first. Transactions do not nest.
    /// </summary>
    public EditTransaction BeginTransaction(string label)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("An edit transaction is already open; commit or cancel it first.");
        var transaction = new EditTransaction(this, label);
        _transaction = transaction;
        return transaction;
    }

    /// <summary>
    /// A cross-command undo group opened by <see cref="EditHistory.BeginTransaction"/>: commands executed while
    /// it is open collapse into one undo entry on <see cref="Commit"/>, or are reverted by <see cref="Cancel"/>.
    /// </summary>
    public sealed class EditTransaction
    {
        private readonly EditHistory _owner;
        private readonly string _label;
        private readonly List<IEditCommand> _commands = [];
        private bool _closed;

        internal EditTransaction(EditHistory owner, string label)
        {
            _owner = owner;
            _label = label;
        }

        /// <summary>The number of commands collected so far.</summary>
        public int Count => _commands.Count;

        internal void Absorb(IEditCommand command) => _commands.Add(command);

        /// <summary>Seals the group as one undo entry (no entry when nothing was executed).</summary>
        public void Commit()
        {
            if (_closed)
                return;
            Close();
            if (_commands.Count == 1)
                _owner._undo.Add(_commands[0]);
            else if (_commands.Count > 1)
                _owner._undo.Add(new CompositeCommand(_label, [.. _commands])); // already applied; redo re-applies
            _owner.Changed?.Invoke();
        }

        /// <summary>Reverts every collected command (in reverse) and discards the group.</summary>
        public void Cancel()
        {
            if (_closed)
                return;
            Close();
            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Revert();
            _owner.Changed?.Invoke();
        }

        internal void Discard()
        {
            if (_closed)
                return;
            Close();
        }

        private void Close()
        {
            _closed = true;
            if (ReferenceEquals(_owner._transaction, this))
                _owner._transaction = null;
        }
    }

    /// <summary>
    /// Opens a coalescing scope: while it (or any nested scope) is open, consecutive compatible commands merge
    /// into one undo entry. Dispose it — e.g. on a slider's pointer-up — to seal the entry so the next gesture
    /// starts a fresh one. Scopes nest; coalescing stays active until the outermost is disposed.
    /// </summary>
    public IDisposable BeginCoalescing()
    {
        _coalescingDepth++;
        return new CoalescingScope(this);
    }

    private static IEditCommand Pop(List<IEditCommand> stack)
    {
        IEditCommand command = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return command;
    }

    private sealed class CoalescingScope(EditHistory owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._coalescingDepth--;
        }
    }
}
