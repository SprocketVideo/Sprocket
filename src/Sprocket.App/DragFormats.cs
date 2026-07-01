using Avalonia.Input;

namespace Sprocket.App;

/// <summary>
/// Custom drag-and-drop data formats shared between the media browser (drag source) and the timeline /
/// inspector (drop targets) for the direct-manipulation editing of PLAN.md step 16b. Keeping them in one
/// place avoids stringly-typed drift between source and target. Uses Avalonia 12's typed
/// <see cref="DataFormat{T}"/> so <c>TryGetValue</c> returns the payload string directly.
/// </summary>
internal static class DragFormats
{
    /// <summary>Payload: a <see cref="Core.Model.MediaRefId"/>'s GUID string — drag a bin tile onto a lane to
    /// place a clip.</summary>
    public static readonly DataFormat<string> MediaRefId =
        DataFormat.CreateStringApplicationFormat("sprocket-media-ref-id");

    /// <summary>Payload: an effect type id string — drag an Effects-browser row onto a clip to append it.</summary>
    public static readonly DataFormat<string> EffectId =
        DataFormat.CreateStringApplicationFormat("sprocket-effect-id");

    /// <summary>Payload: a transition type id string — drag a Transitions-browser row onto a cut between two
    /// clips to apply it (PLAN.md step 25).</summary>
    public static readonly DataFormat<string> TransitionId =
        DataFormat.CreateStringApplicationFormat("sprocket-transition-id");
}
