using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Groups tests that assign the process-wide Serilog logger, so they never run concurrently.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel by default across collections. <c>AppLogger.Initialize</c>
/// replaces <c>Log.Logger</c> for the whole process, so two such tests interleaving would write to
/// each other's directories and fail intermittently. A shared collection is the xUnit mechanism for
/// opting a group out of that parallelism.
/// </remarks>
[CollectionDefinition("AppLogger", DisableParallelization = true)]
public class AppLoggerCollection
{
}