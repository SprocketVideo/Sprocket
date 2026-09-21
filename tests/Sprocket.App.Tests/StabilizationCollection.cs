using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Groups the stabilization tests that set the process-wide <c>SPROCKET_ANALYSIS_DIR</c> environment variable
/// (<see cref="AnalysisCacheTests"/>, <see cref="StabilizationServiceTests"/>) into one collection with
/// parallelization disabled, so they never run concurrently with each other — or with any other collection —
/// and clobber each other's cache directory.
/// </summary>
[CollectionDefinition("Stabilization analysis cache", DisableParallelization = true)]
public sealed class StabilizationCollection;
