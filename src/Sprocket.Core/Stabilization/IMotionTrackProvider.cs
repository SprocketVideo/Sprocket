using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Stabilization;

/// <summary>
/// The seam the render pipeline pulls a clip's motion track through (plan/features/stabilization.md). Core
/// defines it; the App's stabilization service implements it over the per-user analysis cache and the live
/// analysis queue (phase 5). The Render layer holds one on the pipeline and, for each stabilization effect,
/// looks up the track for the frame's media id + source time; a <see langword="null"/> provider or a miss
/// (source not analysed yet) means the effect renders as pass-through until analysis completes.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from the render thread and must not block on analysis — a miss returns
/// <see langword="null"/> immediately (and, in the App, enqueues the analysis). The returned track is immutable.
/// </remarks>
public interface IMotionTrackProvider
{
    /// <summary>
    /// The motion track covering <paramref name="sourceTime"/> for source <paramref name="mediaRefId"/> at the
    /// requested analysis detail, or <see langword="null"/> if none is available yet (not analysed, or the
    /// analysed range doesn't cover the time). Never throws for a miss.
    /// </summary>
    MotionTrack? TryGetTrack(MediaRefId mediaRefId, Timecode sourceTime, bool detailed);
}
