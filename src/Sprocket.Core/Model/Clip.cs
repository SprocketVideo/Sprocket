using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>What a clip's frame is reconstructed from (PLAN.md step 19).</summary>
public enum ClipKind
{
    /// <summary>Decoded from a source media file in the <see cref="MediaPool"/> (the default).</summary>
    Media,

    /// <summary>Produced procedurally by a <see cref="GeneratorSpec"/> (title/text, colour matte) — no source media.</summary>
    Generator,

    /// <summary>An adjustment layer: no content of its own; its effect stack applies to the composite of every
    /// track beneath it for its time span (ARCHITECTURE.md §5, modelled like leading editors' adjustment layers).</summary>
    Adjustment,

    /// <summary>A nested sequence / compound clip: the clip's content is another <see cref="Sequence"/> rendered
    /// at the mapped source time (PLAN.md step 23). The referenced sequence is named by
    /// <see cref="Clip.SourceSequenceId"/> — a reference, not a copy.</summary>
    Sequence,

    /// <summary>A multicam clip (PLAN.md step 24): the clip's content is one selectable angle of a synced
    /// <see cref="MulticamSource"/> (named by <see cref="Clip.SourceMulticamId"/>). The shown angle is
    /// <see cref="Clip.ActiveAngle"/>; switching angles lays down cuts (each segment carries its own active
    /// angle). The active angle resolves to an ordinary source frame at the synced time.</summary>
    Multicam,
}

/// <summary>
/// How a clip's content is conformed to the sequence frame when their aspect ratios differ — the editorial
/// framing policy (Resolve's timeline "Input Scaling" mismatched-resolution options; Premiere's Set to Frame
/// Size). The conform picks the clip's base destination rectangle on the canvas; the Transform effect remains
/// the manual reframing override applied on top of it.
/// </summary>
public enum ClipConformMode
{
    /// <summary>Scale to fit entirely inside the frame, letterboxing/pillarboxing the remainder (the default).</summary>
    Fit,

    /// <summary>Scale to cover the whole frame, cropping the overflow (centre crop).</summary>
    Fill,
}

/// <summary>
/// A non-destructive placement of a portion of a source on a track (ARCHITECTURE.md §4).
/// The source bytes are never modified: trimming edits <see cref="SourceIn"/>/<see cref="SourceOut"/>,
/// moving edits <see cref="TimelineStart"/>, and effects are an additive ordered list. The frame at
/// any timeline time is reconstructed on demand from these descriptors.
/// A clip may instead be a <see cref="ClipKind.Generator"/> (procedural content), a
/// <see cref="ClipKind.Adjustment"/> layer (effects over the tracks below), or a <see cref="ClipKind.Sequence"/>
/// (a nested sequence, PLAN.md step 23) — none has source media but all trim / move / stack and carry effects
/// like any clip (PLAN.md step 19).
/// </summary>
public sealed class Clip
{
    /// <summary>Creates a media clip referencing a source span and placing it on the timeline.</summary>
    public Clip(MediaRefId mediaRefId, Timecode sourceIn, Timecode sourceOut, Timecode timelineStart)
        : this(ClipKind.Media, mediaRefId, generator: null, sourceSequenceId: null, sourceMulticamId: null,
            sourceIn, sourceOut, timelineStart)
    {
    }

    private Clip(ClipKind kind, MediaRefId mediaRefId, GeneratorSpec? generator, SequenceId? sourceSequenceId,
        MulticamId? sourceMulticamId, Timecode sourceIn, Timecode sourceOut, Timecode timelineStart)
    {
        if (sourceOut < sourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(sourceOut));

        Kind = kind;
        MediaRefId = mediaRefId;
        Generator = generator;
        SourceSequenceId = sourceSequenceId;
        SourceMulticamId = sourceMulticamId;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        TimelineStart = timelineStart;
    }

    /// <summary>
    /// Creates a generator clip (PLAN.md step 19): a synthetic source spanning <c>[0, <paramref name="duration"/>)</c>,
    /// produced by <paramref name="generator"/>. Trimming/slipping behaves like media (the synthetic source is
    /// unbounded), and the clip can carry effects.
    /// </summary>
    public static Clip CreateGenerator(GeneratorSpec generator, Timecode duration, Timecode timelineStart)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new Clip(ClipKind.Generator, default, generator, sourceSequenceId: null, sourceMulticamId: null,
            Timecode.Zero, duration, timelineStart);
    }

    /// <summary>
    /// Creates an adjustment-layer clip (PLAN.md step 19): no content of its own, spanning
    /// <c>[0, <paramref name="duration"/>)</c>; its <see cref="Effects"/> apply to the composite of the tracks
    /// beneath it over the clip's time span.
    /// </summary>
    public static Clip CreateAdjustment(Timecode duration, Timecode timelineStart) =>
        new(ClipKind.Adjustment, default, generator: null, sourceSequenceId: null, sourceMulticamId: null,
            Timecode.Zero, duration, timelineStart);

    /// <summary>
    /// Creates a nested-sequence clip (PLAN.md step 23): its content is the sequence identified by
    /// <paramref name="sourceSequenceId"/>, placed over <c>[0, <paramref name="duration"/>)</c> in the child's
    /// time. Trimming/slipping behaves like media (the child timeline is the source span); the clip carries
    /// effects, opacity, and blend like any clip — so editing it edits the nested sequence as one unit.
    /// </summary>
    public static Clip CreateSequenceClip(SequenceId sourceSequenceId, Timecode duration, Timecode timelineStart) =>
        new(ClipKind.Sequence, default, generator: null, sourceSequenceId, sourceMulticamId: null,
            Timecode.Zero, duration, timelineStart);

    /// <summary>
    /// Creates a multicam clip (PLAN.md step 24): its content is one selectable angle of the synced
    /// <see cref="MulticamSource"/> identified by <paramref name="sourceMulticamId"/>, showing
    /// <paramref name="activeAngle"/> over <c>[0, <paramref name="duration"/>)</c> in multicam time. Trimming /
    /// slipping behaves like media (the multicam source is the source span); the clip carries effects, opacity,
    /// and blend like any clip. Switching angles splits the clip and sets the new segment's
    /// <see cref="ActiveAngle"/>.
    /// </summary>
    public static Clip CreateMulticamClip(MulticamId sourceMulticamId, int activeAngle, Timecode duration, Timecode timelineStart) =>
        new(ClipKind.Multicam, default, generator: null, sourceSequenceId: null, sourceMulticamId,
            Timecode.Zero, duration, timelineStart) { ActiveAngle = activeAngle };

    /// <summary>What this clip's frame is reconstructed from.</summary>
    public ClipKind Kind { get; }

    /// <summary>The generator producing this clip's content, or <see langword="null"/> unless <see cref="Kind"/> is
    /// <see cref="ClipKind.Generator"/>.</summary>
    public GeneratorSpec? Generator { get; }

    /// <summary>The nested sequence this clip renders, or <see langword="null"/> unless <see cref="Kind"/> is
    /// <see cref="ClipKind.Sequence"/> (PLAN.md step 23). A reference by id into <see cref="Project.Sequences"/>.</summary>
    public SequenceId? SourceSequenceId { get; }

    /// <summary>The multicam source this clip draws an angle from, or <see langword="null"/> unless
    /// <see cref="Kind"/> is <see cref="ClipKind.Multicam"/> (PLAN.md step 24). A reference by id into
    /// <see cref="Project.MulticamSources"/>.</summary>
    public MulticamId? SourceMulticamId { get; }

    /// <summary>Which angle (by index into <see cref="MulticamSource.Angles"/>) this multicam clip shows
    /// (PLAN.md step 24). Unused for non-multicam clips. Switching angles = split the clip and set the new
    /// segment's active angle, so an angle program is just the run of multicam segments on the track.</summary>
    public int ActiveAngle { get; set; }

    /// <summary>Which source (by id) this clip draws from. Unused (default) for generator / adjustment clips.</summary>
    public MediaRefId MediaRefId { get; set; }

    /// <summary>In-point within the SOURCE (non-destructive trim).</summary>
    public Timecode SourceIn { get; set; }

    /// <summary>Out-point within the SOURCE (exclusive).</summary>
    public Timecode SourceOut { get; set; }

    /// <summary>Where the clip sits on the timeline.</summary>
    public Timecode TimelineStart { get; set; }

    /// <summary>
    /// Per-clip audio gain in decibels (0 dB = unity), applied to the clip's audio on top of any fade and the
    /// track gain (PLAN.md step 30). This is the model gain that clip-scope loudness normalization sets. Audio
    /// only — it has no effect on a video/generator/adjustment clip's pixels.
    /// </summary>
    public double GainDb { get; set; }

    private Rational _speedRatio = Rational.One;

    /// <summary>
    /// Constant playback speed as an exact ratio of source time to timeline time (retime, PLAN.md step 21): 1/1 =
    /// normal, 2/1 = double speed (the source span plays in half the timeline span), 1/2 = half-speed slow motion.
    /// Must be strictly positive. Non-destructive: the source bytes and the selected source span
    /// (<see cref="SourceIn"/>/<see cref="SourceOut"/>) are untouched — only the clip's timeline
    /// <see cref="Duration"/> and the <see cref="MapToSource"/> time map derive from it. Direction is separate
    /// (<see cref="Reverse"/>), and a keyframed <see cref="SpeedCurve"/> takes precedence over this constant
    /// (the defined precedence: hold ▸ ramp ▸ constant speed).
    /// </summary>
    /// <remarks>Freeze-frame (speed 0) is the step-43 frame hold, not a speed value.</remarks>
    public Rational SpeedRatio
    {
        get => _speedRatio;
        set
        {
            if (value.Num <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "SpeedRatio must be strictly positive.");
            _speedRatio = value;
        }
    }

    /// <summary>
    /// Reverse playback (PLAN.md step 21 remainder): the clip walks its source span backwards — the last frame
    /// of the span shows first and the frame at <see cref="SourceIn"/> last — at the same speed the forward map
    /// would use, so the <see cref="Duration"/> is unchanged. Purely a change of time map: the source span is
    /// untouched. Video decodes backwards through the GOP-aware reverse feed and audio plays the source PCM
    /// backwards, both behind the existing seams. Modelled as a flag beside the (always positive) speed, the way
    /// leading editors expose "Reverse Speed" next to the percentage, rather than as a negative ratio.
    /// </summary>
    /// <remarks>A reversed <see cref="MapToSource"/> value is an <em>exclusive</em> upper bound (the clip start maps
    /// to <see cref="SourceOut"/>, the exclusive out-point), so frame providers take the latest frame strictly
    /// <em>below</em> it — timeline frame <c>k</c> then mirrors exactly to source frame <c>N−1−k</c>. Not supported
    /// on nested-sequence clips (<see cref="SupportsReverse"/>): the child sub-mix is planned forward.</remarks>
    public bool Reverse { get; set; }

    /// <summary>Whether this clip can be reversed: every kind except a nested sequence, whose audio sub-mix is
    /// planned forward (PLAN.md step 23 — nested retime is deferred).</summary>
    public bool SupportsReverse => Kind != ClipKind.Sequence;

    private AnimatableValue? _speedCurve;
    // Memo of the ramp-derived duration, keyed on its only inputs: the (immutable) curve instance and the source span.
    private AnimatableValue? _rampMemoCurve;
    private long _rampMemoSpan = -1;
    private long _rampMemoDuration;

    /// <summary>
    /// A keyframed speed ramp (PLAN.md step 21 remainder), or <see langword="null"/> for a constant
    /// <see cref="SpeedRatio"/>. Values are speed as a fraction of normal (1.0 = 100%, clamped to
    /// [<see cref="SpeedRamp.MinSpeed"/>, <see cref="SpeedRamp.MaxSpeed"/>]) and keyframe times are
    /// <b>clip-local ticks</b> (0 = <see cref="TimelineStart"/>) — see <see cref="SpeedRamp"/>. When set and
    /// animated it replaces the constant speed: the time map becomes the integral of the curve and the clip's
    /// <see cref="Duration"/> derives from where that integral covers the source span. A non-animated curve is
    /// normalised to <see langword="null"/> (a constant belongs in <see cref="SpeedRatio"/>).
    /// </summary>
    public AnimatableValue? SpeedCurve
    {
        get => _speedCurve;
        set => _speedCurve = value is { IsAnimated: true } ? value : null;
    }

    /// <summary>Whether the clip's speed is keyframed (<see cref="SpeedCurve"/> is set).</summary>
    public bool HasSpeedRamp => _speedCurve is not null;

    /// <summary>
    /// Whether the clip plays (PLAN.md step 53, the Enable toggle found in leading editors): a disabled clip renders nothing
    /// and contributes no audio, but keeps its place on the timeline — edit logic (trim/snap/move) still sees
    /// it, so only the render graph consults this flag. Toggled through the command stack
    /// (<see cref="Commands.SetPropertyCommand{T}"/>), so it is undoable like every model edit.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Identifies a linked-clip group (PLAN.md step 13, UI.md §3.2). Clips that share a non-null
    /// <see cref="LinkGroupId"/> are companion A/V — a video clip and its source's audio — and the editor
    /// moves / blades them together when "Linked" is on. <see langword="null"/> means the clip is unlinked.
    /// </summary>
    public Guid? LinkGroupId { get; set; }

    private Timecode? _holdFrameAt;

    /// <summary>
    /// Freeze-frame (PLAN.md step 43): when non-null, the clip shows the single source frame at this
    /// <em>source</em> time for its whole timeline span — <see cref="MapToSource"/> becomes a constant map and
    /// <see cref="Duration"/> becomes the independent <see cref="HoldDuration"/>. Modelled as a hold field, not
    /// speed 0, so the <see cref="SpeedRatio"/> &gt; 0 invariant and the derived-duration formula stay intact:
    /// a held clip simply <em>ignores</em> its speed ratio (the defined precedence), and
    /// <see cref="SourceIn"/>/<see cref="SourceOut"/> are retained untouched for exact un-hold. Video holds
    /// only — a linked audio companion clip keeps playing normally, matching the convention in leading editors.
    /// </summary>
    public Timecode? HoldFrameAt
    {
        get => _holdFrameAt;
        set
        {
            if (value is { Ticks: < 0 })
                throw new ArgumentOutOfRangeException(nameof(value), "The held source time must be non-negative.");
            _holdFrameAt = value;
        }
    }

    private Timecode _holdDuration;

    /// <summary>
    /// The held clip's independent timeline duration (PLAN.md step 43) — meaningful only while
    /// <see cref="HoldFrameAt"/> is set. A frozen frame has no media length to derive a duration from, so the
    /// span is free like a generator's: trimming a held clip edits this with no media clamp. Must be
    /// non-negative.
    /// </summary>
    public Timecode HoldDuration
    {
        get => _holdDuration;
        set
        {
            if (value.Ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The hold duration must be non-negative.");
            _holdDuration = value;
        }
    }

    /// <summary>Whether the clip is a frame hold (<see cref="HoldFrameAt"/> is set, PLAN.md step 43).</summary>
    public bool IsHeld => _holdFrameAt is not null;

    /// <summary>
    /// How this clip's content conforms to the sequence frame when their aspect ratios differ
    /// (<see cref="ClipConformMode.Fit"/> letterboxes — the default and the historical behaviour;
    /// <see cref="ClipConformMode.Fill"/> centre-crops). Consulted by the render layer when it computes the
    /// clip's base destination rectangle; the Transform effect composes on top. Video presentation only —
    /// audio and adjustment/generator clips (which render at sequence size) ignore it. Toggled through the
    /// command stack (<see cref="Commands.SetPropertyCommand{T}"/>), so it is undoable like every model edit.
    /// </summary>
    public ClipConformMode ConformMode { get; set; }

    /// <summary>Ordered effect stack, applied bottom→top (ARCHITECTURE.md §5d).</summary>
    public List<EffectInstance> Effects { get; } = new();

    /// <summary>Clip markers, positioned within the clip's source (so they move/trim with the clip). Edited
    /// through the command stack, drawn on the clip body, and listed in the markers panel (PLAN.md step 20).</summary>
    public List<Marker> Markers { get; } = new();

    /// <summary>
    /// Duration on the timeline, derived from the trimmed source span and the playback speed: at a constant
    /// <see cref="SpeedRatio"/> it is <c>(SourceOut - SourceIn) / Speed</c> (so a 2× clip is half as long, a ½×
    /// clip twice as long — at the default 1/1 speed simply the source span); with a <see cref="SpeedCurve"/> it is
    /// the clip-local time at which the ramp's integrated map covers the span (<see cref="SpeedRamp.SolveDuration"/>).
    /// <see cref="Reverse"/> does not change it. A held clip's duration is the independent
    /// <see cref="HoldDuration"/> instead (PLAN.md step 43) — un-hold restores the derived value exactly since
    /// the source span and speed are untouched.
    /// </summary>
    public Timecode Duration
    {
        get
        {
            if (_holdFrameAt is not null)
                return _holdDuration;
            if (_speedCurve is null)
                return (SourceOut - SourceIn).Scale(_speedRatio.Inverse());
            long span = (SourceOut - SourceIn).Ticks;
            if (!ReferenceEquals(_rampMemoCurve, _speedCurve) || _rampMemoSpan != span)
            {
                _rampMemoDuration = SpeedRamp.SolveDuration(_speedCurve, span);
                _rampMemoCurve = _speedCurve;
                _rampMemoSpan = span;
            }
            return new Timecode(_rampMemoDuration);
        }
    }

    /// <summary>Exclusive end of the clip on the timeline (<see cref="TimelineStart"/> + <see cref="Duration"/>).</summary>
    public Timecode TimelineEnd => TimelineStart + Duration;

    /// <summary>Whether the clip is active at timeline time <paramref name="t"/> (start inclusive, end exclusive).</summary>
    public bool Contains(Timecode t) => t >= TimelineStart && t < TimelineEnd;

    /// <summary>
    /// Maps a timeline time within this clip to the corresponding time within the source
    /// (ARCHITECTURE.md §5b). Forward at a constant speed: <c>sourceT = SourceIn + (t - TimelineStart) × Speed</c>
    /// (the plain <c>SourceIn + (t - TimelineStart)</c> at 1/1); with a <see cref="SpeedCurve"/> the offset is the
    /// ramp's integral (<see cref="SpeedRamp.SourceOffsetAt"/>). <see cref="Reverse"/> mirrors the offset from the
    /// out-point: <c>sourceT = SourceOut - offset</c>, so the clip's first frame is the last of the span and the
    /// exclusive out-point maps naturally to "the latest frame before it" in every frame provider. The map is
    /// deliberately <em>not</em> clamped to the span: times outside the clip map to the source beyond its
    /// in/out points (the handles a transition blends, PLAN.md step 25) — in reverse, mirrored. A held
    /// clip maps every time to the constant <see cref="HoldFrameAt"/> (PLAN.md step 43), so preview and export
    /// render the identical frame across the span with no extra render-graph plumbing.
    /// </summary>
    public Timecode MapToSource(Timecode t)
    {
        if (_holdFrameAt is { } held)
            return held;
        Timecode offset = SourceOffset(t);
        return Reverse ? SourceOut - offset : SourceIn + offset;
    }

    /// <summary>
    /// The <em>forward</em> source offset the time map has consumed at timeline time <paramref name="t"/> —
    /// how far into the span (from <see cref="SourceIn"/> forward, or from <see cref="SourceOut"/> backward when
    /// reversed) the clip has walked. Negative before the clip start and past the span after its end (unclamped,
    /// like <see cref="MapToSource"/>). Direction-independent; the piece of the map edit logic (trims, splits)
    /// reasons about.
    /// </summary>
    public Timecode SourceOffset(Timecode t)
    {
        long local = (t - TimelineStart).Ticks;
        long offset = _speedCurve is null
            ? new Timecode(local).Scale(_speedRatio).Ticks
            : SpeedRamp.SourceOffsetAt(_speedCurve, local);
        return new Timecode(offset);
    }

    /// <summary>
    /// A new clip of the same <see cref="Kind"/> and content (media id / cloned generator) over the given span and
    /// placement, <em>without</em> effects or link group. The blade split uses this for the right-hand half, and
    /// duplicate/paste paths use it as the content base, so the new clip keeps a media/generator/adjustment clip's
    /// nature (PLAN.md steps 13/19). A frame hold is content too, so it is copied (PLAN.md step 43) — the blade
    /// split then sets each half's own <see cref="HoldDuration"/>. Speed, direction and the speed curve are copied
    /// as-is (the curve is clip-local, so a right-hand half re-anchors it — see <see cref="Commands.SplitClipCommand"/>).
    /// </summary>
    public Clip CloneContentForSpan(Timecode sourceIn, Timecode sourceOut, Timecode timelineStart) =>
        new(Kind, MediaRefId, Generator?.Clone(), SourceSequenceId, SourceMulticamId, sourceIn, sourceOut, timelineStart)
        {
            SpeedRatio = _speedRatio, Reverse = Reverse, SpeedCurve = _speedCurve, ActiveAngle = ActiveAngle,
            GainDb = GainDb, Enabled = Enabled,
            HoldFrameAt = _holdFrameAt, HoldDuration = _holdDuration, ConformMode = ConformMode,
        };
}
