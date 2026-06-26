namespace Sprocket.Core.Commands;

/// <summary>
/// A reversible set of a single model property, captured as get/set delegates so one command type covers any
/// scalar property (track gain, clip start, track opacity, mute/solo, …) without a bespoke class per field.
/// Pass a <paramref name="mergeKey"/> to make a continuous gesture coalesce: two commands with equal, non-null
/// merge keys collapse into one undo entry inside an <see cref="EditHistory.BeginCoalescing"/> scope (e.g. a
/// slider drag keyed on <c>(track, "GainDb")</c>). Omit it and every set is its own undo step.
/// </summary>
/// <typeparam name="T">The property type.</typeparam>
public sealed class SetPropertyCommand<T> : EditCommand
{
    private readonly Action<T> _setter;
    private readonly T _oldValue;
    private T _newValue;
    private readonly object? _mergeKey;

    private SetPropertyCommand(string label, T oldValue, T newValue, Action<T> setter, object? mergeKey)
        : base(label)
    {
        _setter = setter;
        _oldValue = oldValue;
        _newValue = newValue;
        _mergeKey = mergeKey;
    }

    /// <summary>
    /// Creates a command that will set the property to <paramref name="newValue"/>. The current value is read
    /// from <paramref name="getter"/> now and restored on undo.
    /// </summary>
    public static SetPropertyCommand<T> Create(
        string label, Func<T> getter, Action<T> setter, T newValue, object? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);
        return new SetPropertyCommand<T>(label, getter(), newValue, setter, mergeKey);
    }

    /// <inheritdoc />
    public override void Apply() => _setter(_newValue);

    /// <inheritdoc />
    public override void Revert() => _setter(_oldValue);

    /// <inheritdoc />
    public override bool TryMergeWith(IEditCommand next)
    {
        if (_mergeKey is not null
            && next is SetPropertyCommand<T> other
            && other._mergeKey is not null
            && _mergeKey.Equals(other._mergeKey))
        {
            // Keep our original old value (the gesture's start) and adopt the latest target value.
            _newValue = other._newValue;
            return true;
        }
        return false;
    }
}
