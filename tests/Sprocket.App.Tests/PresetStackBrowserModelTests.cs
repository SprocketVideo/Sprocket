using Sprocket.App.MediaBrowser;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The Effects browser's TOY CASSETTE CAMERA group (plan/features/toy-cassette-camera.md, phase 5): the group lists the
/// stacks with their "Inspired by…" tooltips, the row's drag payload resolves back on the drop, and the shared apply
/// path the double-click and the timeline drop both take applies across a linked pair as one undo step. The row /
/// drag wiring rests on these plus manual verification.
/// </summary>
public class PresetStackBrowserModelTests
{
    private static Project LinkedPair(out Clip video, out Clip audio)
    {
        var project = new Project();
        var v = new VideoTrack();
        var a = new AudioTrack();
        Guid link = Guid.NewGuid();
        MediaRefId media = MediaRefId.New();
        video = new Clip(media, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero) { LinkGroupId = link };
        audio = new Clip(media, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero) { LinkGroupId = link };
        v.Clips.Add(video);
        a.Clips.Add(audio);
        project.Timeline.Tracks.Add(v);
        project.Timeline.Tracks.Add(a);
        return project;
    }

    [Fact]
    public void Browser_Group_Lists_The_Toy_Cassette_Camera_Stacks()
    {
        (string header, IReadOnlyList<PresetStack> stacks) =
            Assert.Single(PresetStackBrowserModel.Groups(PresetStackCatalog.All), g => g.Header == "TOY CASSETTE CAMERA");
        Assert.Equal(new[] { "Clean", "Worn Tape", "Low Light" }, stacks.Select(s => s.Name));
        Assert.Equal("TOY CASSETTE CAMERA", header);
    }

    [Fact]
    public void Rows_Carry_The_Inspired_By_Tooltip_And_A_Video_Plus_Audio_Badge()
    {
        foreach (PresetStack stack in ToyCassetteCameraStacks.All)
        {
            string tip = PresetStackBrowserModel.Tooltip(stack);
            Assert.StartsWith("Inspired by the Fisher-Price PXL 2000", tip);
            Assert.Contains("Double-click", tip);
            Assert.Equal("Video + Audio", PresetStackBrowserModel.Badge(stack));
        }
    }

    [Fact]
    public void Drag_Payload_Resolves_Back_To_The_Same_Stack()
    {
        foreach (PresetStack stack in PresetStackCatalog.All)
            Assert.Same(stack, PresetStackCatalog.Find(PresetStackBrowserModel.DragPayload(stack)));
    }

    [Fact]
    public void Drop_Applies_To_The_Linked_Pair_As_One_Undo_Step()
    {
        Project project = LinkedPair(out Clip video, out Clip audio);
        var history = new EditHistory();

        string status = PresetStackBrowserModel.ApplyToClip(ToyCassetteCameraStacks.WornTape, audio, project, history);

        Assert.Equal("Applied Toy Cassette Camera ▸ Worn Tape on clip.", status);
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, video.Effects.Select(e => e.EffectTypeId));
        Assert.Equal(new[] { EffectTypeIds.AudioCassette }, audio.Effects.Select(e => e.EffectTypeId));
        Assert.Equal(1, history.UndoCount);
        history.Undo();
        Assert.Empty(video.Effects);
        Assert.Empty(audio.Effects);
    }

    [Fact]
    public void Reapply_Reports_Updated_And_Unlinked_Reports_The_Skip()
    {
        Project project = LinkedPair(out Clip video, out Clip audio);
        var history = new EditHistory();
        PresetStackBrowserModel.ApplyToClip(ToyCassetteCameraStacks.Clean, video, project, history);
        Assert.StartsWith("Updated ", PresetStackBrowserModel.ApplyToClip(ToyCassetteCameraStacks.LowLight, video, project, history));
        Assert.Equal(4, video.Effects.Count + audio.Effects.Count);

        video.LinkGroupId = null;
        string status = PresetStackBrowserModel.ApplyToClip(ToyCassetteCameraStacks.Clean, video, project, history);
        Assert.Contains("Skipped Cassette (no linked audio clip)", status);
    }
}
