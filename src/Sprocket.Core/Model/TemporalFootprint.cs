using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// How many <em>earlier</em> frames of its own clip a temporal effect reads, and how far apart they are
/// (plan/features/toy-cassette-camera.md, phase 6). A descriptor supplies one through
/// <see cref="EffectDescriptor.TemporalFootprint"/>, computed from the effect's resolved parameters; the render
/// planner then maps each prior time <c>t + k·Spacing</c> (k = 1…<see cref="Count"/>) through the clip's video time
/// map and hands the source times to the renderer as data. Every frame therefore stays a pure function of
/// (project, time) — no feedback buffer — so scrubbing, seeking, the render cache and export all agree (§5, §8).
/// </summary>
/// <param name="Count">How many prior frames (0 = none). The planner caps the total across one layer at
/// <see cref="MaxPriorFrames"/>.</param>
/// <param name="Spacing">Signed timeline distance between successive prior frames (negative = earlier).</param>
public readonly record struct TemporalFootprint(int Count, Timecode Spacing)
{
    /// <summary>The most prior frames one layer may read, across all of its temporal effects — the number of
    /// extra shader inputs a temporal stage can bind, and the bound on per-layer frame history.</summary>
    public const int MaxPriorFrames = 8;

    /// <summary>No prior frames.</summary>
    public static TemporalFootprint None => default;

    /// <summary>Whether this footprint reads any prior frame.</summary>
    public bool IsEmpty => Count <= 0 || Spacing.Ticks == 0;
}

/// <summary>The <see cref="EffectTypeIds.Echo"/> operators (<see cref="EffectParamNames.EchoOperator"/>), After
/// Effects' names and order. The parameter stores the index.</summary>
public static class EchoOperators
{
    /// <summary>Sums the echoes onto the current frame (bright, can clip).</summary>
    public const int Add = 0;

    /// <summary>Keeps the brightest value per channel — trails that never darken.</summary>
    public const int Maximum = 1;

    /// <summary>Keeps the darkest value per channel.</summary>
    public const int Minimum = 2;

    /// <summary>Screens the echoes over the current frame (a softer, non-clipping Add).</summary>
    public const int Screen = 3;

    /// <summary>Composites each echo behind the frames before it, by alpha.</summary>
    public const int CompositeInBack = 4;

    /// <summary>Composites each echo in front of the frames before it, by alpha.</summary>
    public const int CompositeInFront = 5;

    /// <summary>Averages the current frame and its echoes.</summary>
    public const int Blend = 6;

    /// <summary>Display names by index — the Inspector's dropdown choices.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["Add", "Maximum", "Minimum", "Screen", "Composite in Back", "Composite in Front", "Blend"];
}
