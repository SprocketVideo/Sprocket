using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic tests for nested-sequence audio mixing (PLAN.md step 23): a nested-sequence clip's audio is the
/// child sequence's sub-mix, and the nesting clip's gain/fade applies over that whole unit. Uses synthetic
/// <see cref="FakePcmReader"/>s (no FFmpeg).
/// </summary>
public class NestedAudioMixerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static readonly MediaRefId ChildMedia = MediaRefId.New();

    /// <summary>Active "Parent" sequence whose audio track nests a "Child" sequence carrying one media audio clip.
    /// <paramref name="parentGainDb"/> is the gain on the parent's (nesting) track.</summary>
    private static Project ParentNestingChildAudio(double parentGainDb = 0)
    {
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(640, 480), Rate);
        var childTrack = new AudioTrack { Name = "A1" };
        childTrack.Clips.Add(new Clip(ChildMedia, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);

        var parentTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(parentTimeline);
        project.Sequences.Add(child);
        var parentTrack = new AudioTrack { Name = "A1", GainDb = parentGainDb };
        parentTrack.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero));
        parentTimeline.Tracks.Add(parentTrack);
        return project;
    }

    private static AudioMixer MixerFor(float childValue) =>
        new(Rate, Channels, id => id == ChildMedia ? new FakePcmReader(Rate, Channels, childValue) : null);

    [Fact]
    public void Nested_Sequence_Audio_Reaches_The_Mix()
    {
        using AudioMixer mixer = MixerFor(0.4f);
        var buffer = new float[256 * Channels];
        mixer.MixInto(buffer, Timecode.Zero, ParentNestingChildAudio());
        Assert.All(buffer, s => Assert.Equal(0.4f, s, 0.0001));
    }

    [Fact]
    public void Nesting_Track_Gain_Applies_To_The_Whole_Nested_Sub_Mix()
    {
        using AudioMixer mixer = MixerFor(0.8f);
        var buffer = new float[256 * Channels];
        // -6.0206 dB ≈ 0.5 linear on the nesting track → 0.8 × 0.5 = 0.4.
        mixer.MixInto(buffer, Timecode.Zero, ParentNestingChildAudio(parentGainDb: -6.0206));
        Assert.All(buffer, s => Assert.Equal(0.4f, s, 0.01));
    }
}
