using System;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>Headless tests for <see cref="SourceRangePayload"/>, the Source-monitor drag payload (PLAN.md step 61
/// phase 4).</summary>
public class SourceRangePayloadTests
{
    [Fact]
    public void SourceRangePayload_Round_Trips_And_Rejects_Malformed_Text()
    {
        var payload = new SourceRangePayload(MediaRefId.New(), Timecode.FromSeconds(1), Timecode.FromSeconds(3), SourceStreams.Audio);

        Assert.Equal(payload, SourceRangePayload.TryParse(payload.Format()));
        Assert.Null(SourceRangePayload.TryParse(null));
        Assert.Null(SourceRangePayload.TryParse("not-a-guid|0|10|Both"));
        Assert.Null(SourceRangePayload.TryParse($"{Guid.NewGuid()}|10|10|Both"));   // empty range
        Assert.Null(SourceRangePayload.TryParse($"{Guid.NewGuid()}|0|10|Sideways"));
        Assert.Null(SourceRangePayload.TryParse($"{Guid.NewGuid()}|0|10|7"));       // numeric, undefined
    }
}
