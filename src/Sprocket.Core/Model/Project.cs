using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>Project-wide settings. Kept minimal for the slice; grows without touching the model shape.</summary>
public sealed class ProjectSettings
{
    /// <summary>Master output gain in decibels, applied to the final audio mix (0 dB = unity).</summary>
    public double MasterGainDb { get; set; }
}

/// <summary>
/// The root of the editor's document model (ARCHITECTURE.md §4): the imported media, the timeline
/// being edited, and project settings. Plain data with no native handles, so it serializes cleanly
/// and round-trips in tests.
/// </summary>
public sealed class Project
{
    /// <summary>Creates an empty project with a 1080p / 30fps / 48kHz default timeline.</summary>
    public Project()
        : this(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000))
    {
    }

    /// <summary>Creates a project with the given timeline.</summary>
    public Project(Timeline timeline) => Timeline = timeline;

    /// <summary>Imported source media, addressed by stable id.</summary>
    public MediaPool MediaPool { get; } = new();

    /// <summary>The timeline being edited.</summary>
    public Timeline Timeline { get; }

    /// <summary>Project-wide settings.</summary>
    public ProjectSettings Settings { get; } = new();
}
