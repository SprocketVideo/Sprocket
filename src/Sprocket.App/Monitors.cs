using System;
using System.Threading.Tasks;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// A previewable, transport-driven monitor (PLAN.md step 17): the Program monitor (the composited timeline) and
/// the Source monitor (a raw single-clip preview) are presented through one shared <see cref="PreviewSurface"/>
/// + render graph and expose an identical transport, so one transport bar can drive whichever monitor is active
/// (UI.md §3.4). The shell binds <see cref="CurrentEngine"/> (at <see cref="FrameWidth"/>×<see cref="FrameHeight"/>)
/// to the surface and re-binds when <see cref="EngineChanged"/> fires; position/state events let the shell update
/// its readouts only for the active monitor.
/// </summary>
internal interface IMonitor
{
    /// <summary>The engine to present, or <c>null</c> when nothing is loaded (the surface shows its background).</summary>
    PlaybackEngine? CurrentEngine { get; }

    /// <summary>The logical frame size to composite into (the sequence resolution / the source resolution).</summary>
    int FrameWidth { get; }
    int FrameHeight { get; }

    Timecode Position { get; }
    Timecode Duration { get; }
    PlaybackState State { get; }

    void Play();
    void Pause();
    void TogglePlayPause();
    void SeekTo(Timecode position);
    void StepFrame(int delta);
    void JumpToStart();
    void JumpToEnd();

    event Action<Timecode>? PositionChanged;
    event Action<PlaybackState>? StateChanged;

    /// <summary>Raised when <see cref="CurrentEngine"/> is replaced (the Source monitor rebuilds/tears down its
    /// engine); the shell re-binds the surface. The Program monitor never raises it.</summary>
    event Action? EngineChanged;
}

/// <summary>The Program monitor: a thin transport adapter over the app's main <see cref="PlaybackEngine"/>, which
/// composites every enabled video track at the playhead (PLAN.md step 14).</summary>
internal sealed class ProgramMonitor : IMonitor
{
    private readonly PlaybackEngine _engine;

    public ProgramMonitor(PlaybackEngine engine, int frameWidth, int frameHeight)
    {
        _engine = engine;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
    }

    public PlaybackEngine? CurrentEngine => _engine;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    /// <summary>Re-points the logical frame size after the open (active) sequence's resolution changes (PLAN.md
    /// step 23). The engine composites at the live <c>project.Timeline.Resolution</c>; this only updates the size
    /// the preview surface letterboxes / overlays against.</summary>
    public void SetFrameSize(int frameWidth, int frameHeight)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
    }

    public Timecode Position => _engine.Position;
    public Timecode Duration => _engine.Duration;
    public PlaybackState State => _engine.State;

    public void Play() => _engine.Play();
    public void Pause() => _engine.Pause();
    public void TogglePlayPause() => _engine.TogglePlayPause();
    public void SeekTo(Timecode position) => _engine.SeekTo(position);
    public void StepFrame(int delta) => _engine.StepFrame(delta);
    public void JumpToStart() => _engine.SeekTo(Timecode.Zero);
    public void JumpToEnd() => _engine.SeekTo(_engine.Duration);

    public event Action<Timecode>? PositionChanged
    {
        add => _engine.PositionChanged += value;
        remove => _engine.PositionChanged -= value;
    }

    public event Action<PlaybackState>? StateChanged
    {
        add => _engine.StateChanged += value;
        remove => _engine.StateChanged -= value;
    }

    public event Action? EngineChanged { add { } remove { } } // the program engine is fixed
}

/// <summary>
/// The Source monitor (PLAN.md step 17, UI.md §3.4): a raw preview of a single selected source media for setting
/// in/out before editing. It owns a rebuildable single-feed <see cref="PlaybackEngine"/> over a throwaway one-clip
/// project that spans the whole source (the same render graph as the Program monitor, ARCHITECTURE.md §5). The
/// engine is built only while the Source tab is <see cref="Activate">active</see> so a decoder is opened lazily and
/// freed when the user looks away; the preview is video-only on a <see cref="SoftwareClock"/> (source-audio scrub
/// is a later refinement). Decode/playback is device/IO-bound, so this rests on manual verification.
/// </summary>
internal sealed class SourceMonitor : IMonitor, IAsyncDisposable
{
    private PlaybackEngine? _engine;
    private MediaRef? _desired;   // the source the shell wants previewed
    private MediaRef? _shown;     // the source the current engine is decoding
    private bool _active;

    public PlaybackEngine? CurrentEngine => _engine;
    public int FrameWidth => _shown?.Info.Width ?? 0;
    public int FrameHeight => _shown?.Info.Height ?? 0;
    public Timecode Position => _engine?.Position ?? Timecode.Zero;
    public Timecode Duration => _engine?.Duration ?? Timecode.Zero;
    public PlaybackState State => _engine?.State ?? PlaybackState.Stopped;

    public event Action<Timecode>? PositionChanged;
    public event Action<PlaybackState>? StateChanged;
    public event Action? EngineChanged;

    /// <summary>Sets the source to preview (typically the selected clip's media). Rebuilds the engine if the
    /// Source tab is currently active; otherwise the build is deferred to <see cref="Activate"/>.</summary>
    public void SetSource(MediaRef? media)
    {
        _desired = media;
        if (_active)
            Rebuild();
    }

    /// <summary>Builds + starts the source engine (if a source is set) and begins presenting it. Called when the
    /// Source tab is opened.</summary>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        Rebuild();
    }

    /// <summary>Tears the source engine down to free its decoder. Called when the Source tab is left.</summary>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        Teardown();
    }

    private void Rebuild()
    {
        if (ReferenceEquals(_desired, _shown) && _engine is not null)
            return;
        Teardown();

        if (_desired is not { Info.HasVideo: true } media)
            return;
        IVideoFrameFeed? feed = MediaBootstrap.OpenVideoFeed(media);
        if (feed is null)
            return;

        var engine = new PlaybackEngine(BuildSourceProject(media), feed); // default SoftwareClock, video-only
        engine.PositionChanged += OnEnginePosition;
        engine.StateChanged += OnEngineState;
        engine.Start();

        _engine = engine;
        _shown = media;
        EngineChanged?.Invoke(); // shell binds it to the shared surface
        StateChanged?.Invoke(engine.State);
        PositionChanged?.Invoke(engine.Position);
    }

    private void Teardown()
    {
        bool hadEngine = _engine is not null;
        if (_engine is { } engine)
        {
            engine.PositionChanged -= OnEnginePosition;
            engine.StateChanged -= OnEngineState;
            _engine = null;
            _ = engine.DisposeAsync(); // fire-and-forget: stops the pump + disposes the feed
        }
        _shown = null;
        if (hadEngine)
            EngineChanged?.Invoke(); // shell detaches the surface
    }

    private void OnEnginePosition(Timecode t) => PositionChanged?.Invoke(t);
    private void OnEngineState(PlaybackState s) => StateChanged?.Invoke(s);

    /// <summary>A throwaway project: one video track holding one clip that spans the whole source, raw (no
    /// effects). The single-feed engine drives that track from the supplied feed.</summary>
    private static Project BuildSourceProject(MediaRef media)
    {
        ProbedMediaInfo info = media.Info;
        int sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
        var project = new Project(timeline);
        project.MediaPool.Add(media);

        var track = new VideoTrack { Name = "Source" };
        track.Clips.Add(new Clip(media.Id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);
        return project;
    }

    public void Play() => _engine?.Play();
    public void Pause() => _engine?.Pause();
    public void TogglePlayPause() => _engine?.TogglePlayPause();
    public void SeekTo(Timecode position) => _engine?.SeekTo(position);
    public void StepFrame(int delta) => _engine?.StepFrame(delta);
    public void JumpToStart() => _engine?.SeekTo(Timecode.Zero);
    public void JumpToEnd() { if (_engine is { } e) e.SeekTo(e.Duration); }

    public async ValueTask DisposeAsync()
    {
        if (_engine is { } engine)
        {
            engine.PositionChanged -= OnEnginePosition;
            engine.StateChanged -= OnEngineState;
            _engine = null;
            await engine.DisposeAsync();
        }
    }
}
