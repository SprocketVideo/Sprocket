using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests the pure sequence-naming helper (PLAN.md step 23): "New Sequence" / "Nest" pick the first
/// <c>"{prefix} N"</c> not already taken, so names stay unique and gap-filling, case-insensitively.
/// </summary>
public class SequenceNamingTests
{
    private static Project ProjectWith(params string[] names)
    {
        // The first sequence is the project's default; rename it, then add the rest.
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.ActiveSequence.Name = names.Length > 0 ? names[0] : "Sequence 1";
        for (int i = 1; i < names.Length; i++)
            project.Sequences.Add(new Sequence(SequenceId.New(), names[i],
                new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000)));
        return project;
    }

    [Fact]
    public void Picks_The_Next_Free_Number()
    {
        Project project = ProjectWith("Sequence 1");
        Assert.Equal("Sequence 2", SequenceNaming.NextUnique(project, "Sequence"));
    }

    [Fact]
    public void Fills_The_Lowest_Gap()
    {
        // 1 and 3 are taken; the next free is 2.
        Project project = ProjectWith("Sequence 1", "Sequence 3");
        Assert.Equal("Sequence 2", SequenceNaming.NextUnique(project, "Sequence"));
    }

    [Fact]
    public void Is_Case_Insensitive()
    {
        Project project = ProjectWith("sequence 1");
        Assert.Equal("Sequence 2", SequenceNaming.NextUnique(project, "Sequence"));
    }

    [Fact]
    public void Honours_A_Distinct_Prefix()
    {
        // The "Sequence N" names don't collide with the "Nested Sequence" prefix.
        Project project = ProjectWith("Sequence 1", "Sequence 2");
        Assert.Equal("Nested Sequence 1", SequenceNaming.NextUnique(project, "Nested Sequence"));
    }
}
