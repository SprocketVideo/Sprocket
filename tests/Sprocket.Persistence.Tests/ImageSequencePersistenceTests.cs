using System.IO;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Persistence of image-sequence / still media (PLAN.md step 42): the additive <c>MediaRefDto</c> fields round-trip,
/// ordinary files serialize byte-identically to pre-42, and a moved sequence relinks by rewriting the pattern's
/// directory along with its first-frame path.
/// </summary>
public class ImageSequencePersistenceTests
{
    private static ProbedMediaInfo VideoInfo(Rational fps, Timecode duration) =>
        new(duration, HasVideo: true, fps, 1920, 1080, HasAudio: false, SampleRate: 0, Channels: 0);

    private static MediaRef Find(Project p, MediaRefId id) => p.MediaPool.Get(id)!;

    [Fact]
    public void SequenceFieldsRoundTrip()
    {
        var project = new Project();
        var fps = new Rational(12, 1);
        var media = new MediaRef(MediaRefId.New(), @"C:\shot\frame_0001.png", VideoInfo(fps, Timecode.FromFrames(240, fps)))
        {
            Kind = MediaKind.ImageSequence,
            SequencePattern = @"C:\shot\frame_%04d.png",
            SequenceStartNumber = 1,
            SequenceFrameCount = 240,
        };
        project.MediaPool.Add(media);

        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        MediaRef r = Find(loaded, media.Id);

        Assert.Equal(MediaKind.ImageSequence, r.Kind);
        Assert.Equal(@"C:\shot\frame_%04d.png", r.SequencePattern);
        Assert.Equal(1, r.SequenceStartNumber);
        Assert.Equal(240, r.SequenceFrameCount);
        Assert.Equal(fps, r.Info.FrameRate);
        Assert.Equal(Timecode.FromFrames(240, fps).Ticks, r.Info.Duration.Ticks);
    }

    [Fact]
    public void StillKindRoundTrips()
    {
        var project = new Project();
        var media = new MediaRef(MediaRefId.New(), @"C:\art\logo.png",
            VideoInfo(new Rational(25, 1), Timecode.FromSeconds(5))) { Kind = MediaKind.Still };
        project.MediaPool.Add(media);

        MediaRef r = Find(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)), media.Id);
        Assert.Equal(MediaKind.Still, r.Kind);
        Assert.True(r.HasUnboundedDuration);
    }

    [Fact]
    public void OrdinaryFileWritesNoKindFields()
    {
        var project = new Project();
        var media = new MediaRef(MediaRefId.New(), @"C:\v\clip.mp4",
            VideoInfo(new Rational(30, 1), Timecode.FromSeconds(10)));
        project.MediaPool.Add(media);

        string json = ProjectSerializer.Serialize(project);

        // Byte-identical to pre-42: none of the new keys appear for a plain file.
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("sequencePattern", json);
        Assert.DoesNotContain("sequenceStartNumber", json);
        Assert.DoesNotContain("sequenceFrameCount", json);
    }

    [Fact]
    public void RelinkRewritesSequencePatternDirectory()
    {
        // Build a project whose sequence's first frame lives in a folder we then "move".
        string oldDir = Path.Combine(Path.GetTempPath(), "sprk_seq_old_" + System.Guid.NewGuid().ToString("N"));
        string newDir = Path.Combine(Path.GetTempPath(), "sprk_seq_new_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(newDir);
        try
        {
            // Only the new folder exists (old is offline). Put the matching first-frame file there.
            File.WriteAllText(Path.Combine(newDir, "frame_0001.png"), "x");

            var project = new Project();
            var fps = new Rational(12, 1);
            var media = new MediaRef(MediaRefId.New(), Path.Combine(oldDir, "frame_0001.png"),
                VideoInfo(fps, Timecode.FromFrames(5, fps)))
            {
                Kind = MediaKind.ImageSequence,
                SequencePattern = Path.Combine(oldDir, "frame_%04d.png"),
                SequenceStartNumber = 1,
                SequenceFrameCount = 5,
            };
            project.MediaPool.Add(media);

            RelinkPlan plan = MediaRelink.Plan(project, newDir);
            int relinked = MediaRelink.Apply(project, plan);

            Assert.Equal(1, relinked);
            Assert.Equal(Path.Combine(newDir, "frame_0001.png"), media.AbsolutePath);
            Assert.Equal(Path.Combine(newDir, "frame_%04d.png"), media.SequencePattern);
        }
        finally
        {
            try { Directory.Delete(newDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
