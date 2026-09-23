using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Persistence;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// The Looks browser's library (plan/features/looks-browser.md): the curated built-ins (<see cref="LooksCatalog"/>)
/// followed by the user's saved looks, which it owns and persists to <c>looks.json</c> under the per-platform
/// application-data folder through <see cref="LooksStore"/> — the <c>UserExportPresets</c> split. Every mutation
/// saves immediately and raises <see cref="Changed"/> so the browser re-lists. User looks are app-wide, not
/// per-project (like Premiere's Lumetri presets and Resolve's PowerGrade album), and live outside the
/// <see cref="Core.Commands.EditHistory"/>: saving, renaming or deleting a look edits the library, not the project.
/// </summary>
internal sealed class LooksLibrary
{
    /// <summary>The looks file for this user: <c>%AppData%/Sprocket/looks.json</c> (or the platform equivalent).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sprocket", "looks.json");

    private readonly string _path;
    private readonly List<Look> _user;
    private bool _backupPending; // back the file up before the first save overwrites it (see the constructor)

    /// <summary>Loads the user looks from <paramref name="path"/> (defaults to <see cref="DefaultPath"/>).</summary>
    public LooksLibrary(string? path = null)
    {
        _path = path ?? DefaultPath;
        LooksLoadResult loaded = LooksStore.Load(_path);
        _user = [.. loaded.Looks];
        LoadWarnings = loaded.Warnings;
        // A file that loaded with repairs or not at all still holds the user's work; the first save would overwrite
        // it with what survived, so keep the original beside it first.
        _backupPending = loaded.Warnings.Count > 0 && File.Exists(_path);
    }

    /// <summary>Where the pre-repair copy of an unreadable or repaired looks file is kept before it is first overwritten.</summary>
    public string BackupPath => _path + ".bak";

    /// <summary>Notes from loading the file (entries repaired or dropped), for a one-time status hint.</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>Raised after any add / rename / remove.</summary>
    public event Action? Changed;

    /// <summary>The user's saved looks, in the order they were saved.</summary>
    public IReadOnlyList<Look> UserLooks => _user;

    /// <summary>Everything the browser lists: built-ins first, then the user's own.</summary>
    public IReadOnlyList<Look> All => LooksStore.BuiltInAndUser(_user);

    /// <summary>The look with id <paramref name="id"/> (built-in or user), or <see langword="null"/>.</summary>
    public Look? Find(string id) => LooksCatalog.Find(id) ?? _user.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// Adds a user look (always filed under <see cref="Look.UserGroup"/> with a fresh user id) and saves. A name
    /// already used by another user look gets a " 2", " 3" … suffix, so two saved looks are never ambiguous in the
    /// list. Returns the look as stored. Whether the file write succeeded is reported by <see cref="LastSaveFailed"/>.
    /// </summary>
    public Look Add(Look look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var stored = look with { Id = Look.NewUserId(), Group = Look.UserGroup, Name = UniqueName(look.Name) };
        _user.Add(stored);
        Persist();
        return stored;
    }

    /// <summary>Renames the user look <paramref name="id"/> (built-ins are read-only). Returns the stored look, or
    /// <see langword="null"/> when there is no such user look or the name is blank.</summary>
    public Look? Rename(string id, string name)
    {
        int index = _user.FindIndex(l => l.Id == id);
        if (index < 0 || string.IsNullOrWhiteSpace(name))
            return null;
        Look renamed = _user[index] with { Name = UniqueName(name, exceptId: id) };
        _user[index] = renamed;
        Persist();
        return renamed;
    }

    /// <summary>Deletes the user look <paramref name="id"/> (built-ins cannot be removed). Returns whether it existed.</summary>
    public bool Remove(string id)
    {
        if (_user.RemoveAll(l => l.Id == id) == 0)
            return false;
        Persist();
        return true;
    }

    /// <summary>Whether the most recent save failed to write the file (the change still holds for this session).</summary>
    public bool LastSaveFailed { get; private set; }

    /// <summary><paramref name="name"/> trimmed, or with the first free " N" suffix if another user look (other than
    /// <paramref name="exceptId"/>) already has it, compared case-insensitively.</summary>
    public string UniqueName(string name, string? exceptId = null)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "Look" : name.Trim();
        bool Taken(string candidate) => _user.Any(l => l.Id != exceptId
            && string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(trimmed))
            return trimmed;
        for (int n = 2; ; n++)
        {
            string candidate = $"{trimmed} {n}";
            if (!Taken(candidate))
                return candidate;
        }
    }

    private void Persist()
    {
        if (_backupPending)
        {
            try
            {
                File.Copy(_path, BackupPath, overwrite: true);
                _backupPending = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never overwrite a file we could not back up: the change holds for this session only.
                LastSaveFailed = true;
                Changed?.Invoke();
                return;
            }
        }
        LastSaveFailed = !LooksStore.Save(_path, _user);
        Changed?.Invoke();
    }
}
