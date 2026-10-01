namespace Sprocket.Core.Model;

/// <summary>Which of a source's streams a drag from the Source monitor carries (PLAN.md step 61 phase 4).</summary>
public enum SourceStreams
{
    /// <summary>Video and audio — dragging the Source picture itself.</summary>
    Both,

    /// <summary>Video only — Premiere's "Drag Video Only" handle.</summary>
    Video,

    /// <summary>Audio only — Premiere's "Drag Audio Only" handle.</summary>
    Audio,
}
