using Sprocket.App;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests the pure Sequence Settings apply logic (<see cref="SequenceSettingsOps.BuildCommand"/>): no-ops
/// return null, a single change returns a single command, and a combined name + format change is one
/// <see cref="CompositeCommand"/> — so a dialog Apply is always exactly one undo entry.
/// </summary>
public class SequenceSettingsOpsTests
{
    private static Project NewProject() => new();

    [Fact]
    public void Unchanged_Result_Builds_Nothing()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        Assert.Null(SequenceSettingsOps.BuildCommand(sequence, sequence.Name, sequence.Timeline.Resolution));
    }

    [Fact]
    public void Blank_Or_Whitespace_Name_Counts_As_Unchanged()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        Assert.Null(SequenceSettingsOps.BuildCommand(sequence, "   ", sequence.Timeline.Resolution));
    }

    [Fact]
    public void Rename_Only_Is_A_Single_Undo_Step()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        string oldName = sequence.Name;
        var history = new EditHistory();

        IEditCommand? command = SequenceSettingsOps.BuildCommand(sequence, "Cut A", sequence.Timeline.Resolution);
        Assert.NotNull(command);
        history.Execute(command);
        Assert.Equal("Cut A", sequence.Name);

        history.Undo();
        Assert.Equal(oldName, sequence.Name);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Format_Only_Is_A_Single_Undo_Step()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        var history = new EditHistory();

        IEditCommand? command = SequenceSettingsOps.BuildCommand(sequence, sequence.Name, new Resolution(1080, 1920));
        Assert.NotNull(command);
        history.Execute(command);
        Assert.Equal(new Resolution(1080, 1920), sequence.Timeline.Resolution);

        history.Undo();
        Assert.Equal(new Resolution(1920, 1080), sequence.Timeline.Resolution);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Name_Plus_Format_Is_One_Composite_Undo_Entry()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        string oldName = sequence.Name;
        var history = new EditHistory();

        IEditCommand? command = SequenceSettingsOps.BuildCommand(sequence, "Vertical Cut", new Resolution(1080, 1920));
        Assert.IsType<CompositeCommand>(command);
        history.Execute(command!);
        Assert.Equal("Vertical Cut", sequence.Name);
        Assert.Equal(new Resolution(1080, 1920), sequence.Timeline.Resolution);

        history.Undo(); // one undo restores both
        Assert.Equal(oldName, sequence.Name);
        Assert.Equal(new Resolution(1920, 1080), sequence.Timeline.Resolution);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Name_Is_Trimmed_Before_Comparison_And_Apply()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;

        // Same name with surrounding whitespace is unchanged…
        Assert.Null(SequenceSettingsOps.BuildCommand(sequence, $"  {sequence.Name}  ", sequence.Timeline.Resolution));

        // …and a genuinely new name lands trimmed.
        IEditCommand? command = SequenceSettingsOps.BuildCommand(sequence, "  Cut B  ", sequence.Timeline.Resolution);
        Assert.NotNull(command);
        command!.Apply();
        Assert.Equal("Cut B", sequence.Name);
    }

    [Fact]
    public void Degenerate_Resolution_Is_Ignored()
    {
        Project project = NewProject();
        Sequence sequence = project.ActiveSequence;
        Assert.Null(SequenceSettingsOps.BuildCommand(sequence, sequence.Name, new Resolution(0, 1080)));
    }
}
