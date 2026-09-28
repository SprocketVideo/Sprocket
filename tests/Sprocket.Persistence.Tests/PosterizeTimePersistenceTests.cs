using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Posterize Time on the wire (plan/features/toy-cassette-camera.md phase 2): it is an ordinary effect entry —
/// no DTO change — so a chain carrying it round-trips with its rate, order and enabled flag, the reloaded clip
/// posterizes identically, and (the render-cache hash covering the effect chain) adding it invalidates cached
/// renders.
/// </summary>
public class PosterizeTimePersistenceTests
{
    private static Project BuildProject(out Clip clip)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(1));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return project;
    }

    private static EffectInstance Posterize(double fps) =>
        EffectCatalog.Find(EffectTypeIds.PosterizeTime)!.CreateInstance().Set(EffectParamNames.PosterizeFrameRate, fps);

    [Fact]
    public void A_Chain_With_Posterize_Time_Round_Trips()
    {
        Project project = BuildProject(out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        clip.Effects.Add(Posterize(23.976));
        EffectInstance disabled = Posterize(6);
        disabled.Enabled = false;
        clip.Effects.Add(disabled);

        Clip loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project))
            .Timeline.VideoTracks.First().Clips[0];

        Assert.Equal(
            [EffectTypeIds.Brightness, EffectTypeIds.PosterizeTime, EffectTypeIds.PosterizeTime],
            loaded.Effects.Select(e => e.EffectTypeId));
        Assert.Equal(23.976, loaded.Effects[1].Parameters[EffectParamNames.PosterizeFrameRate].Evaluate(Timecode.Zero));
        Assert.True(loaded.Effects[1].Enabled);
        Assert.False(loaded.Effects[2].Enabled);

        // The reloaded clip reads the same grid (the first enabled entry) and maps video identically.
        Assert.Equal(new Timecode(10010), loaded.PosterizeInterval);
        for (long t = Timecode.TicksPerSecond; t < 2 * Timecode.TicksPerSecond; t += 1234)
            Assert.Equal(clip.MapToSourceVideo(new Timecode(t)), loaded.MapToSourceVideo(new Timecode(t)));
    }

    [Fact]
    public void Adding_Posterize_Time_Changes_The_Render_Cache_Hash()
    {
        Project project = BuildProject(out Clip clip);
        string Hash() => RenderCacheHasher.ComputeHash(
            project, project.ActiveSequence.Id, Timecode.Zero, Timecode.FromSeconds(5), RenderCacheScope.Video);

        string before = Hash();
        clip.Effects.Add(Posterize(15));
        string posterized = Hash();
        Assert.NotEqual(before, posterized);

        clip.Effects[0].Set(EffectParamNames.PosterizeFrameRate, 12.0);
        Assert.NotEqual(posterized, Hash());
    }
}
