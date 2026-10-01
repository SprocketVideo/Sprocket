using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The one stack-position rule every add-effect path shares (<see cref="EffectPlacement"/>): Color Transform
/// leads, Stabilization follows a Color Transform (or leads), everything else appends.
/// </summary>
public class EffectPlacementTests
{
    private static Clip ClipWith(params string[] typeIds)
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        foreach (string id in typeIds)
            clip.Effects.Add(new EffectInstance(id));
        return clip;
    }

    [Theory]
    [InlineData(EffectTypeIds.ColorTransform)]
    [InlineData(EffectTypeIds.Stabilization)]
    [InlineData(EffectTypeIds.Brightness)]
    public void Empty_Stack_Places_Anything_At_Zero(string typeId) =>
        Assert.Equal(0, EffectPlacement.IndexFor(typeId, ClipWith().Effects));

    [Fact]
    public void Color_Transform_Goes_To_The_Front()
    {
        Clip clip = ClipWith(EffectTypeIds.Color, EffectTypeIds.Brightness);
        Assert.Equal(0, EffectPlacement.IndexFor(EffectTypeIds.ColorTransform, clip.Effects));
    }

    [Fact]
    public void Stabilization_Goes_Just_After_An_Existing_Color_Transform()
    {
        Clip clip = ClipWith(EffectTypeIds.ColorTransform, EffectTypeIds.Color, EffectTypeIds.Brightness);
        Assert.Equal(1, EffectPlacement.IndexFor(EffectTypeIds.Stabilization, clip.Effects));
    }

    [Fact]
    public void Stabilization_Goes_To_The_Front_Without_A_Color_Transform()
    {
        Clip clip = ClipWith(EffectTypeIds.Color, EffectTypeIds.Stabilization);
        Assert.Equal(0, EffectPlacement.IndexFor(EffectTypeIds.Stabilization, clip.Effects));
    }

    [Fact]
    public void Anything_Else_Appends()
    {
        Clip clip = ClipWith(EffectTypeIds.ColorTransform, EffectTypeIds.Stabilization, EffectTypeIds.Color);
        Assert.Equal(3, EffectPlacement.IndexFor(EffectTypeIds.Brightness, clip.Effects));
    }

    [Fact]
    public void AddCommand_Inserts_At_The_Rule_Position_And_Undo_Removes()
    {
        Clip clip = ClipWith(EffectTypeIds.ColorTransform, EffectTypeIds.Color);
        var stabilization = new EffectInstance(EffectTypeIds.Stabilization);
        var history = new EditHistory();

        history.Execute(EffectPlacement.AddCommand(clip, stabilization));
        Assert.Same(stabilization, clip.Effects[1]);

        history.Undo();
        Assert.DoesNotContain(stabilization, clip.Effects);
    }
}
