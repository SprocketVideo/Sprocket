using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class AnimatableValueTests
{
    [Fact]
    public void Constant_Ignores_Time()
    {
        var v = AnimatableValue.Constant(0.5);
        Assert.False(v.IsAnimated);
        Assert.Equal(0.5, v.Evaluate(Timecode.Zero));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(100)));
    }

    [Fact]
    public void Linear_Interpolates_Midpoint()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(0), 1.0),
            new Keyframe(Timecode.FromSeconds(2), 0.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(0)));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 6);
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Clamps_Outside_Keyframe_Range()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(1), 0.2),
            new Keyframe(Timecode.FromSeconds(3), 0.8),
        });

        Assert.Equal(0.2, v.Evaluate(Timecode.FromSeconds(-5)));
        Assert.Equal(0.8, v.Evaluate(Timecode.FromSeconds(100)));
    }

    [Fact]
    public void Hold_Steps_Instead_Of_Interpolating()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(0), 1.0, Interpolation.Hold),
            new Keyframe(Timecode.FromSeconds(2), 0.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(1.9)));
        Assert.Equal(0.0, v.Evaluate(Timecode.FromSeconds(2)));
    }

    [Fact]
    public void Keyframes_Are_Sorted_Regardless_Of_Input_Order()
    {
        var v = AnimatableValue.Animated(new[]
        {
            new Keyframe(Timecode.FromSeconds(2), 0.0),
            new Keyframe(Timecode.FromSeconds(0), 1.0),
        });

        Assert.Equal(1.0, v.Evaluate(Timecode.FromSeconds(0)));
        Assert.Equal(0.5, v.Evaluate(Timecode.FromSeconds(1)), 6);
    }

    [Fact]
    public void Empty_Keyframes_Throws()
    {
        Assert.Throws<ArgumentException>(() => AnimatableValue.Animated(Array.Empty<Keyframe>()));
    }
}
