using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Headless tests for multiple sequences + nested-sequence (compound-clip) rendering (PLAN.md step 23): the
/// project's sequence list and active sequence, the nested-clip render-graph recursion, cycle detection, the
/// depth guard, and the <see cref="SequenceGraph"/> cycle helper. All pure model — no Skia, FFmpeg, or GPU.
/// </summary>
public class SequenceModelTests
{
    [Fact]
    public void New_Project_Has_One_Active_Sequence()
    {
        var project = new Project();
        Sequence only = Assert.Single(project.Sequences);
        Assert.Same(only, project.ActiveSequence);
        Assert.Same(only.Timeline, project.Timeline); // Timeline delegates to the active sequence
    }

    [Fact]
    public void Active_Sequence_Must_Be_In_The_Project()
    {
        var project = new Project();
        var stray = new Sequence(SequenceId.New(), "Stray", new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000));
        Assert.Throws<ArgumentException>(() => project.ActiveSequence = stray);
    }

    [Fact]
    public void Get_Sequence_Finds_By_Id_And_Misses_Gracefully()
    {
        var project = new Project();
        Sequence active = project.ActiveSequence;
        Assert.Same(active, project.GetSequence(active.Id));
        Assert.Null(project.GetSequence(SequenceId.New()));
    }

    [Fact]
    public void Sequence_Clip_Factory_Sets_Kind_And_Reference()
    {
        var id = SequenceId.New();
        Clip clip = Clip.CreateSequenceClip(id, Timecode.FromSeconds(5), Timecode.FromSeconds(2));
        Assert.Equal(ClipKind.Sequence, clip.Kind);
        Assert.Equal(id, clip.SourceSequenceId);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
    }
}

public class NestedSequenceRenderTests
{
    private const int Rate = 48000;

    /// <summary>A project whose active "Parent" sequence nests a "Child" sequence (one media clip) as a clip,
    /// returning the child's media id and the nesting clip for assertions.</summary>
    private static Project ParentNestingChild(
        out MediaRefId childMedia, out Clip nestClip,
        Timecode? nestStart = null, Timecode? childMediaIn = null)
    {
        childMedia = MediaRefId.New();
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(1280, 720), Rate);
        var childTrack = new VideoTrack { Name = "V1" };
        childTrack.Clips.Add(new Clip(childMedia, childMediaIn ?? Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);

        var parentTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(parentTimeline); // active = the parent
        project.Sequences.Add(child);

        var parentTrack = new VideoTrack { Name = "V1" };
        nestClip = Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), nestStart ?? Timecode.Zero);
        parentTrack.Clips.Add(nestClip);
        parentTimeline.Tracks.Add(parentTrack);
        return project;
    }

    [Fact]
    public void Nested_Clip_Resolves_The_Child_Sequences_Layers()
    {
        Project project = ParentNestingChild(out MediaRefId childMedia, out _);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));

        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(LayerKind.Sequence, layer.Kind);
        Assert.NotNull(layer.NestedPlan);
        Assert.Equal(new Resolution(1280, 720), layer.NestedPlan!.Resolution); // the child's own format
        VideoLayer childLayer = Assert.Single(layer.NestedPlan.Layers);
        Assert.Equal(LayerKind.Media, childLayer.Kind);
        Assert.Equal(childMedia, childLayer.MediaRefId);
    }

    [Fact]
    public void Nested_Clip_Maps_Parent_Time_Into_The_Child_Timeline()
    {
        // Nest starts at parent 4s; the child media's source in-point is 2s. At parent t=7s the child-local time
        // is 3s, so the child media source time is 2s + 3s = 5s.
        Project project = ParentNestingChild(out _, out _,
            nestStart: Timecode.FromSeconds(4), childMediaIn: Timecode.FromSeconds(2));
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(7));

        VideoLayer nest = Assert.Single(plan.Layers);
        Assert.Equal(Timecode.FromSeconds(3), nest.SourceTime); // child-local time
        VideoLayer childLayer = Assert.Single(nest.NestedPlan!.Layers);
        Assert.Equal(Timecode.FromSeconds(5), childLayer.SourceTime); // mapped into the child media's source
    }

    [Fact]
    public void Nesting_Clip_Effects_And_Track_Compositing_Apply_To_The_Whole_Nested_Unit()
    {
        Project project = ParentNestingChild(out _, out Clip nestClip);
        nestClip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.5));
        project.Timeline.VideoTracks.First().Opacity = 0.6;
        project.Timeline.VideoTracks.First().BlendMode = BlendMode.Screen;

        VideoLayer nest = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);
        Assert.Equal(EffectTypeIds.Brightness, Assert.Single(nest.Effects).EffectTypeId);
        Assert.Equal(0.6, nest.Opacity, 6);
        Assert.Equal(BlendMode.Screen, nest.BlendMode);
    }

    [Fact]
    public void Missing_Sequence_Reference_Contributes_No_Layer()
    {
        var parentTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(parentTimeline);
        var track = new VideoTrack();
        track.Clips.Add(Clip.CreateSequenceClip(SequenceId.New(), Timecode.FromSeconds(5), Timecode.Zero)); // dangling ref
        parentTimeline.Tracks.Add(track);

        Assert.Empty(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);
    }

    [Fact]
    public void Direct_Cycle_Does_Not_Recurse_Forever()
    {
        // A nests B, B nests A. Planning A must terminate: the B nest resolves, but B's nest of A is a cycle and
        // contributes nothing.
        var tlA = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var a = new Sequence(SequenceId.New(), "A", tlA);
        var tlB = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var b = new Sequence(SequenceId.New(), "B", tlB);

        var trackA = new VideoTrack();
        trackA.Clips.Add(Clip.CreateSequenceClip(b.Id, Timecode.FromSeconds(5), Timecode.Zero));
        tlA.Tracks.Add(trackA);
        var trackB = new VideoTrack();
        trackB.Clips.Add(Clip.CreateSequenceClip(a.Id, Timecode.FromSeconds(5), Timecode.Zero));
        tlB.Tracks.Add(trackB);

        var project = new Project(a);
        project.Sequences.Add(b);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        VideoLayer bNest = Assert.Single(plan.Layers);          // A → B resolved
        Assert.Equal(LayerKind.Sequence, bNest.Kind);
        Assert.Empty(bNest.NestedPlan!.Layers);                 // B → A is a cycle: nothing
    }

    [Fact]
    public void Deep_Chain_Stops_At_The_Depth_Guard()
    {
        // A chain of N sequences each nesting the next, N > MaxNestingDepth. Planning must terminate and the
        // resolved nesting depth is capped at the guard (no stack overflow, no runaway).
        const int n = SequenceGraph.MaxNestingDepth + 5;
        var sequences = new List<Sequence>();
        for (int i = 0; i < n; i++)
        {
            var tl = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
            sequences.Add(new Sequence(SequenceId.New(), $"S{i}", tl));
        }
        for (int i = 0; i < n - 1; i++)
        {
            var track = new VideoTrack();
            track.Clips.Add(Clip.CreateSequenceClip(sequences[i + 1].Id, Timecode.FromSeconds(5), Timecode.Zero));
            sequences[i].Timeline.Tracks.Add(track);
        }

        var project = new Project(sequences[0]);
        for (int i = 1; i < n; i++)
            project.Sequences.Add(sequences[i]);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        int depth = NestingDepth(plan);
        Assert.Equal(SequenceGraph.MaxNestingDepth, depth);
    }

    private static int NestingDepth(VideoFramePlan plan)
    {
        int max = 0;
        foreach (VideoLayer layer in plan.Layers)
            if (layer is { Kind: LayerKind.Sequence, NestedPlan: { } nested })
                max = Math.Max(max, 1 + NestingDepth(nested));
        return max;
    }

    [Fact]
    public void Audio_Nested_Clip_Resolves_The_Child_Audio_Plan()
    {
        var childMedia = MediaRefId.New();
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var childAudio = new AudioTrack { Name = "A1" };
        childAudio.Clips.Add(new Clip(childMedia, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(childAudio);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);

        var parentTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(parentTimeline);
        project.Sequences.Add(child);
        var parentAudio = new AudioTrack { Name = "A1" };
        parentAudio.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero));
        parentTimeline.Tracks.Add(parentAudio);

        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        AudioLayer nest = Assert.Single(plan.Layers);
        Assert.NotNull(nest.NestedPlan);
        Assert.Equal(1.0, nest.NestedPlan!.MasterGainLinear, 6); // master gain applied only at the root
        AudioLayer childLayer = Assert.Single(nest.NestedPlan.Layers);
        Assert.Equal(childMedia, childLayer.MediaRefId);
    }
}

public class NestedSequenceExecutorTests
{
    /// <summary>A fake compositor that records composites as strings, so the recursive executor's nesting can be
    /// asserted without Skia (mirrors the StringCompositor in <c>RenderGraphTests</c>).</summary>
    private sealed class StringCompositor : IVideoCompositor<string>
    {
        public List<string> CompositeLog { get; } = new();
        public string CreateTransparentSurface(Resolution size) => "surface[";
        public string CreateGeneratorFrame(ResolvedGenerator g, Resolution size, Timecode t) => $"gen({g.GeneratorTypeId})";
        public string ApplyEffect(string frame, ResolvedEffect e) => $"{frame}+{e.EffectTypeId}";
        public string ApplyTransition(string from, string to, ResolvedTransition t) =>
            $"[{from}>{t.TransitionTypeId}>{to}]";
        public void Composite(string surface, string layer, double opacity, BlendMode blend) =>
            CompositeLog.Add($"{layer}@{opacity:0.##}/{blend}");
        public string Snapshot(string surface) => surface + "]";
    }

    private sealed class NamedFrameSource : IFrameSource<string>
    {
        public string GetFrame(MediaRefId media, Timecode sourceTime) => $"frame({sourceTime.Ticks})";
    }

    [Fact]
    public void Render_Nested_Sequence_Layer_Composites_The_Child_Recursively()
    {
        // Child: one media layer. Parent: one sequence layer wrapping the child plan, with an effect, at 0.5 opacity.
        var childMedia = new VideoLayer(MediaRefId.New(), new Timecode(300), [], 1.0, BlendMode.Normal);
        var childPlan = new VideoFramePlan(new Resolution(320, 240), Timecode.Zero, new[] { childMedia });

        var brightness = new ResolvedEffect(EffectTypeIds.Brightness, new Dictionary<string, double>());
        var nest = new VideoLayer(default, Timecode.Zero, new[] { brightness }, 0.5, BlendMode.Multiply,
            LayerKind.Sequence, NestedPlan: childPlan);
        var parentPlan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { nest });

        var compositor = new StringCompositor();
        RenderGraph.Render(parentPlan, new NamedFrameSource(), compositor);

        // Two composites in order: the child's media frame onto the child surface, then the snapshotted child
        // surface ("surface[]") with the nesting layer's effect folded in, at the nesting opacity/blend.
        Assert.Equal(2, compositor.CompositeLog.Count);
        Assert.Equal("frame(300)@1/Normal", compositor.CompositeLog[0]);
        Assert.Equal("surface[]+builtin.brightness@0.5/Multiply", compositor.CompositeLog[1]);
    }
}

public class SequenceGraphTests
{
    private static Project TwoSequences(out Sequence a, out Sequence b)
    {
        var tlA = new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000);
        a = new Sequence(SequenceId.New(), "A", tlA);
        var tlB = new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000);
        b = new Sequence(SequenceId.New(), "B", tlB);
        var project = new Project(a);
        project.Sequences.Add(b);
        return project;
    }

    private static void Nest(Sequence container, Sequence child)
    {
        var track = new VideoTrack();
        track.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(5), Timecode.Zero));
        container.Timeline.Tracks.Add(track);
    }

    [Fact]
    public void Nesting_A_Sequence_Into_Itself_Is_A_Cycle()
    {
        Project project = TwoSequences(out Sequence a, out _);
        Assert.True(SequenceGraph.WouldCreateCycle(project, container: a.Id, candidateChild: a.Id));
    }

    [Fact]
    public void Nesting_Into_A_Descendant_Is_A_Cycle()
    {
        // A contains B. Putting A inside B would close a loop A → B → A.
        Project project = TwoSequences(out Sequence a, out Sequence b);
        Nest(a, b);
        Assert.True(SequenceGraph.WouldCreateCycle(project, container: b.Id, candidateChild: a.Id));
        // The other direction (B already inside A) does not: nesting B into A again is just reuse.
        Assert.False(SequenceGraph.WouldCreateCycle(project, container: a.Id, candidateChild: b.Id));
    }

    [Fact]
    public void Independent_Sequences_Are_Not_A_Cycle()
    {
        Project project = TwoSequences(out Sequence a, out Sequence b);
        Assert.False(SequenceGraph.WouldCreateCycle(project, container: a.Id, candidateChild: b.Id));
    }
}

public class SequenceCommandTests
{
    [Fact]
    public void Add_Sequence_Command_Adds_And_Reverts()
    {
        var project = new Project();
        var history = new EditHistory();
        var seq = new Sequence(SequenceId.New(), "Nested", new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000));

        history.Execute(new AddSequenceCommand(project, seq));
        Assert.Contains(seq, project.Sequences);

        history.Undo();
        Assert.DoesNotContain(seq, project.Sequences);
        Assert.Same(project.Sequences[0], project.ActiveSequence); // active is untouched

        history.Redo();
        Assert.Contains(seq, project.Sequences);
    }

    [Fact]
    public void Remove_Sequence_Command_Restores_At_Index_On_Undo()
    {
        var project = new Project();
        var history = new EditHistory();
        var first = new Sequence(SequenceId.New(), "First", new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000));
        var second = new Sequence(SequenceId.New(), "Second", new Timeline(new Rational(30, 1), new Resolution(640, 480), 48000));
        project.Sequences.Add(first);
        project.Sequences.Add(second);

        history.Execute(new RemoveSequenceCommand(project, first));
        Assert.DoesNotContain(first, project.Sequences);

        history.Undo();
        Assert.Equal(1, project.Sequences.IndexOf(first)); // restored at its original index (after the default seq)
    }

    [Fact]
    public void Remove_Active_Sequence_Is_Rejected()
    {
        var project = new Project();
        Assert.Throws<InvalidOperationException>(() => new RemoveSequenceCommand(project, project.ActiveSequence));
    }
}

public class SequenceNestingTests
{
    private static (Project project, VideoTrack v, AudioTrack a, Clip video, Clip audio) LinkedAvProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var media = MediaRefId.New();
        var link = Guid.NewGuid();

        var v = new VideoTrack { Name = "V1" };
        var video = new Clip(media, Timecode.Zero, Timecode.FromSeconds(6), Timecode.FromSeconds(2)) { LinkGroupId = link };
        v.Clips.Add(video);
        var a = new AudioTrack { Name = "A1" };
        var audio = new Clip(media, Timecode.Zero, Timecode.FromSeconds(6), Timecode.FromSeconds(2)) { LinkGroupId = link };
        a.Clips.Add(audio);
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        return (project, v, a, video, audio);
    }

    [Fact]
    public void Nest_Moves_Selection_Into_A_New_Child_And_Leaves_A_Linked_Nested_Pair()
    {
        (Project project, VideoTrack v, AudioTrack a, Clip video, Clip audio) = LinkedAvProject();
        var history = new EditHistory();

        SequenceNesting.NestResult? result = SequenceNesting.CreateNest(
            project, project.ActiveSequence, [video, audio], "Nested Sequence 1");
        Assert.NotNull(result);
        history.Execute(result!.Command);

        // The child sequence now holds the two clips, rebased to start at the origin.
        Assert.Equal(2, project.Sequences.Count);
        Sequence child = result.Child;
        Assert.Contains(child, project.Sequences);
        Assert.Equal(Timecode.Zero, child.Timeline.VideoTracks.First().Clips.Single().TimelineStart);
        Assert.Equal(Timecode.Zero, child.Timeline.AudioTracks.First().Clips.Single().TimelineStart);

        // The parent now has a linked video+audio nested-sequence pair where the originals were (at start 2s).
        Clip nestV = v.Clips.Single();
        Clip nestA = a.Clips.Single();
        Assert.Equal(ClipKind.Sequence, nestV.Kind);
        Assert.Equal(child.Id, nestV.SourceSequenceId);
        Assert.Equal(Timecode.FromSeconds(2), nestV.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(6), nestV.Duration); // the selection's full extent
        Assert.NotNull(nestV.LinkGroupId);
        Assert.Equal(nestV.LinkGroupId, nestA.LinkGroupId); // the new pair is linked
    }

    [Fact]
    public void Nest_Is_One_Undoable_Step()
    {
        (Project project, VideoTrack v, AudioTrack a, Clip video, Clip audio) = LinkedAvProject();
        var history = new EditHistory();

        SequenceNesting.NestResult result = SequenceNesting.CreateNest(
            project, project.ActiveSequence, [video, audio], "Nested Sequence 1")!;
        history.Execute(result.Command);

        history.Undo();
        // Everything is restored: the child sequence is gone and the originals are back on their tracks at 2s.
        Assert.Single(project.Sequences);
        Assert.Same(video, v.Clips.Single());
        Assert.Same(audio, a.Clips.Single());
        Assert.Equal(Timecode.FromSeconds(2), video.TimelineStart);
    }

    [Fact]
    public void Nest_Renders_The_Same_Picture_Through_The_New_Nested_Clip()
    {
        // After nesting the video clip, planning the parent yields a nested-sequence layer whose child plan
        // resolves the original media at the same source time it would have had un-nested.
        (Project project, VideoTrack v, _, Clip video, _) = LinkedAvProject();
        MediaRefId media = video.MediaRefId;
        var history = new EditHistory();
        history.Execute(SequenceNesting.CreateNest(project, project.ActiveSequence, [video], "Nested Sequence 1")!.Command);

        // At parent t=3s: the un-nested clip (start 2s, sourceIn 0) would map to source 1s. Through the nest it
        // must map the same: child-local 1s → child clip (start 0, sourceIn 0) → source 1s.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(3));
        VideoLayer nest = Assert.Single(plan.Layers);
        Assert.Equal(LayerKind.Sequence, nest.Kind);
        VideoLayer childLayer = Assert.Single(nest.NestedPlan!.Layers);
        Assert.Equal(media, childLayer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(1), childLayer.SourceTime);
    }
}
