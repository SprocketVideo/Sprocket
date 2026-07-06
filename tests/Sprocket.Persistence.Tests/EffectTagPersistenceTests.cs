using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Effect instance reference tags (<see cref="EffectTags"/>) round-trip through the project file — they're
/// the stable handle MCP clients hold across sessions — and pre-tag files still load (missing tag = null,
/// backfilled by the app's sweep).
/// </summary>
public class EffectTagPersistenceTests
{
    private static (Project Project, Clip Clip) ProjectWithEffects()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Color));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (project, clip);
    }

    [Fact]
    public void Assigned_Tags_Round_Trip()
    {
        (Project project, Clip _) = ProjectWithEffects();
        EffectTags.EnsureAssigned(project);

        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Clip clip = loaded.Timeline.Tracks[0].Clips[0];
        Assert.Equal("BR-1", clip.Effects[0].Tag);
        Assert.Equal("CO-2", clip.Effects[1].Tag);

        // The sweep sees the loaded tags as settled — nothing renumbers on the next session.
        Assert.False(EffectTags.EnsureAssigned(loaded));
    }

    [Fact]
    public void Pre_Tag_Files_Load_With_Null_Tags()
    {
        (Project project, Clip _) = ProjectWithEffects(); // never swept — serialized without tags
        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Clip clip = loaded.Timeline.Tracks[0].Clips[0];
        Assert.Null(clip.Effects[0].Tag);
        Assert.True(EffectTags.EnsureAssigned(loaded)); // and the app's sweep backfills them
        Assert.Equal("BR-1", clip.Effects[0].Tag);
    }
}
