namespace Sprocket.Core.Model;

/// <summary>
/// The set of source media imported into a <see cref="Project"/>, keyed by <see cref="MediaRefId"/>.
/// Clips reference sources by id rather than by object so the model stays serializable and a source
/// can be relinked (offline media) without rewriting clips (ARCHITECTURE.md §4, §12).
/// </summary>
public sealed class MediaPool
{
    private readonly Dictionary<MediaRefId, MediaRef> _byId = new();

    /// <summary>All imported sources.</summary>
    public IReadOnlyCollection<MediaRef> Items => _byId.Values;

    /// <summary>Adds a source. Throws if a source with the same id is already present.</summary>
    public MediaRef Add(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!_byId.TryAdd(media.Id, media))
            throw new InvalidOperationException($"A media reference with id {media.Id} already exists.");
        return media;
    }

    /// <summary>Looks up a source by id, or returns <see langword="null"/> if it is not present.</summary>
    public MediaRef? Get(MediaRefId id) => _byId.GetValueOrDefault(id);

    /// <summary>Attempts to look up a source by id.</summary>
    public bool TryGet(MediaRefId id, out MediaRef media)
    {
        bool found = _byId.TryGetValue(id, out MediaRef? value);
        media = value!;
        return found;
    }

    /// <summary>Removes a source by id. Returns whether it was present.</summary>
    public bool Remove(MediaRefId id) => _byId.Remove(id);
}
