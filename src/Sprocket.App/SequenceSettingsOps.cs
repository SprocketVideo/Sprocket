using System.Collections.Generic;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// Turns a Sequence Settings dialog result into the undoable edit that applies it: nothing when unchanged, a
/// single <see cref="SetPropertyCommand{T}"/> when only the name or only the frame size changed, and a
/// <see cref="CompositeCommand"/> when both did — so an Apply is always exactly one undo entry. A format
/// change deliberately re-scales nothing: like Premiere's Sequence Settings, the canvas changes and every
/// clip keeps its framing (each clip's conform mode + Transform decide how it fills the new frame). Pure
/// decision logic, kept out of MainWindow so it is unit-testable headlessly.
/// </summary>
public static class SequenceSettingsOps
{
    /// <summary>
    /// Builds the command applying <paramref name="newName"/> + <paramref name="newResolution"/> to
    /// <paramref name="sequence"/>, or <see langword="null"/> when neither differs (a blank name counts
    /// as unchanged — the dialog never returns one, but the guard keeps this total).
    /// </summary>
    public static IEditCommand? BuildCommand(Sequence sequence, string newName, Resolution newResolution)
    {
        var commands = new List<IEditCommand>(2);

        string trimmed = newName.Trim();
        if (trimmed.Length > 0 && trimmed != sequence.Name)
            commands.Add(SetPropertyCommand<string>.Create(
                "Rename sequence", () => sequence.Name, v => sequence.Name = v, trimmed));

        Timeline timeline = sequence.Timeline;
        if (newResolution != timeline.Resolution
            && newResolution.Width > 0 && newResolution.Height > 0)
            commands.Add(SetPropertyCommand<Resolution>.Create(
                "Change sequence format", () => timeline.Resolution, v => timeline.Resolution = v, newResolution));

        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand("Sequence settings", commands),
        };
    }
}
