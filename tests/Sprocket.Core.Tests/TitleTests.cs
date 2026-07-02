using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Covers the rich-titles model of PLAN.md step 40: the scroll math (<see cref="TitleScroll"/>), the
/// clip-local progress carried on <see cref="ResolvedGenerator"/>, the new catalog templates
/// (Lower Third / Credits Roll / Crawl), and the generator-parameter edit commands.
/// </summary>
public class TitleTests
{
    // ── Scroll math ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void Eased_No_Easing_Is_Linear(double p, double expected) =>
        Assert.Equal(expected, TitleScroll.Eased(p, easeIn: false, easeOut: false), 9);

    [Fact]
    public void Eased_EaseIn_Starts_Slow()
    {
        Assert.Equal(0.0, TitleScroll.Eased(0.0, true, false), 9);
        Assert.Equal(0.0625, TitleScroll.Eased(0.25, true, false), 9); // p²
        Assert.Equal(1.0, TitleScroll.Eased(1.0, true, false), 9);
    }

    [Fact]
    public void Eased_EaseOut_Ends_Slow()
    {
        Assert.Equal(0.0, TitleScroll.Eased(0.0, false, true), 9);
        Assert.Equal(0.9375, TitleScroll.Eased(0.75, false, true), 9); // 1-(1-p)²
        Assert.Equal(1.0, TitleScroll.Eased(1.0, false, true), 9);
    }

    [Fact]
    public void Eased_Both_Is_Smoothstep_And_Symmetric()
    {
        Assert.Equal(0.5, TitleScroll.Eased(0.5, true, true), 9); // 3p²-2p³ at 0.5
        Assert.Equal(
            1.0 - TitleScroll.Eased(0.25, true, true),
            TitleScroll.Eased(0.75, true, true), 9);
    }

    [Fact]
    public void Eased_Clamps_Outside_Range()
    {
        Assert.Equal(0.0, TitleScroll.Eased(-1.0, false, false), 9);
        Assert.Equal(1.0, TitleScroll.Eased(2.0, true, true), 9);
    }

    // ── Clip-local progress on the resolved generator ───────────────────────────────────────────────

    private static Clip TitleClipAt(double startSeconds, double durationSeconds) =>
        Clip.CreateGenerator(
            new GeneratorSpec(GeneratorTypeIds.Roll).SetString(GeneratorParamNames.Text, "T"),
            Timecode.FromSeconds(durationSeconds), Timecode.FromSeconds(startSeconds));

    [Theory]
    [InlineData(10.0, 0.0)]  // at the clip's start
    [InlineData(12.0, 0.5)]  // midway
    [InlineData(14.0, 1.0)]  // at the clip's end
    [InlineData(5.0, 0.0)]   // before → clamped
    [InlineData(20.0, 1.0)]  // after → clamped
    public void ResolveGenerator_Clip_Carries_Local_Progress(double atSeconds, double expected)
    {
        Clip clip = TitleClipAt(10.0, 4.0);
        ResolvedGenerator resolved = RenderGraph.ResolveGenerator(clip, Timecode.FromSeconds(atSeconds));
        Assert.Equal(expected, resolved.Progress, 9);
    }

    [Fact]
    public void ResolveGenerator_Spec_Overload_Has_Zero_Progress()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).SetString(GeneratorParamNames.Text, "T");
        Assert.Equal(0.0, RenderGraph.ResolveGenerator(spec, Timecode.FromSeconds(3)).Progress, 9);
    }

    [Fact]
    public void PlanVideoFrame_Generator_Layer_Carries_Progress()
    {
        var project = new Project();
        var track = new VideoTrack();
        track.Clips.Add(TitleClipAt(0.0, 4.0));
        project.Timeline.Tracks.Add(track);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(0.25, layer.Generator!.Progress, 9);
    }

    // ── Catalog templates ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsTitle_Covers_The_Title_Family_Only()
    {
        Assert.True(GeneratorTypeIds.IsTitle(GeneratorTypeIds.Title));
        Assert.True(GeneratorTypeIds.IsTitle(GeneratorTypeIds.LowerThird));
        Assert.True(GeneratorTypeIds.IsTitle(GeneratorTypeIds.Roll));
        Assert.True(GeneratorTypeIds.IsTitle(GeneratorTypeIds.Crawl));
        Assert.False(GeneratorTypeIds.IsTitle(GeneratorTypeIds.SolidColor));
        Assert.False(GeneratorTypeIds.IsTitle("plugin.custom.gen"));
    }

    [Fact]
    public void Catalog_Registers_The_Title_Templates_With_Their_Scroll_Defaults()
    {
        GeneratorDescriptor lower = Assert.Single(GeneratorCatalog.BuiltIns, d => d.Id == GeneratorTypeIds.LowerThird);
        GeneratorSpec lowerSpec = lower.CreateSpec();
        Assert.Equal("left", lowerSpec.GetString(GeneratorParamNames.Alignment));
        Assert.NotEqual("", lowerSpec.GetString(GeneratorParamNames.Text2));
        Assert.NotEqual("", lowerSpec.GetString(GeneratorParamNames.BoxColor));

        GeneratorDescriptor roll = Assert.Single(GeneratorCatalog.BuiltIns, d => d.Id == GeneratorTypeIds.Roll);
        Assert.Equal(TitleScrollModes.Roll, roll.CreateSpec().GetString(GeneratorParamNames.ScrollMode));

        GeneratorDescriptor crawl = Assert.Single(GeneratorCatalog.BuiltIns, d => d.Id == GeneratorTypeIds.Crawl);
        Assert.Equal(TitleScrollModes.Crawl, crawl.CreateSpec().GetString(GeneratorParamNames.ScrollMode));
    }

    // ── Generator parameter commands ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetGeneratorParameter_Applies_And_Reverts_To_Absent()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title);
        var cmd = new SetGeneratorParameterCommand(spec, GeneratorParamNames.Tracking, AnimatableValue.Constant(0.1));

        cmd.Apply();
        Assert.Equal(0.1, spec.Parameters[GeneratorParamNames.Tracking].Evaluate(Timecode.Zero), 9);

        cmd.Revert();
        Assert.False(spec.Parameters.ContainsKey(GeneratorParamNames.Tracking)); // was absent before
    }

    [Fact]
    public void SetGeneratorParameter_Reverts_To_Previous_Value()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).Set(GeneratorParamNames.FontSize, 0.12);
        var cmd = new SetGeneratorParameterCommand(spec, GeneratorParamNames.FontSize, AnimatableValue.Constant(0.2));

        cmd.Apply();
        Assert.Equal(0.2, spec.Parameters[GeneratorParamNames.FontSize].Evaluate(Timecode.Zero), 9);
        cmd.Revert();
        Assert.Equal(0.12, spec.Parameters[GeneratorParamNames.FontSize].Evaluate(Timecode.Zero), 9);
    }

    [Fact]
    public void SetGeneratorParameter_Drag_Coalesces_To_One_Undo_Entry()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).Set(GeneratorParamNames.FontSize, 0.1);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetGeneratorParameterCommand(spec, GeneratorParamNames.FontSize, AnimatableValue.Constant(0.15)));
            history.Execute(new SetGeneratorParameterCommand(spec, GeneratorParamNames.FontSize, AnimatableValue.Constant(0.3)));
        }

        Assert.Equal(1, history.UndoCount);
        history.Undo();
        Assert.Equal(0.1, spec.Parameters[GeneratorParamNames.FontSize].Evaluate(Timecode.Zero), 9);
    }

    [Fact]
    public void SetGeneratorString_Applies_Reverts_And_Removes_On_Empty()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).SetString(GeneratorParamNames.Text, "Old");

        var set = new SetGeneratorStringCommand(spec, GeneratorParamNames.Text, "New");
        set.Apply();
        Assert.Equal("New", spec.GetString(GeneratorParamNames.Text));
        set.Revert();
        Assert.Equal("Old", spec.GetString(GeneratorParamNames.Text));

        var clear = new SetGeneratorStringCommand(spec, GeneratorParamNames.Bold, "");
        clear.Apply();
        Assert.False(spec.Strings.ContainsKey(GeneratorParamNames.Bold)); // empty = absent (step-19 default)

        var setNew = new SetGeneratorStringCommand(spec, GeneratorParamNames.Bold, "true");
        setNew.Apply();
        Assert.Equal("true", spec.GetString(GeneratorParamNames.Bold));
        setNew.Revert();
        Assert.False(spec.Strings.ContainsKey(GeneratorParamNames.Bold)); // was absent before
    }

    [Fact]
    public void SetGeneratorString_Typing_Coalesces_To_One_Undo_Entry()
    {
        var spec = new GeneratorSpec(GeneratorTypeIds.Title).SetString(GeneratorParamNames.Text, "T");
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetGeneratorStringCommand(spec, GeneratorParamNames.Text, "Ti"));
            history.Execute(new SetGeneratorStringCommand(spec, GeneratorParamNames.Text, "Tit"));
            history.Execute(new SetGeneratorStringCommand(spec, GeneratorParamNames.Text, "Title"));
        }

        Assert.Equal(1, history.UndoCount);
        history.Undo();
        Assert.Equal("T", spec.GetString(GeneratorParamNames.Text));
    }
}
