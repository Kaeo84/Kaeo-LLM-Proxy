using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Opt-in verification against the developer's real application database, which is a different
/// shape of risk from the synthetic fixtures in <see cref="AppDatabaseSchemaTests"/>: it is hundreds
/// of megabytes of accumulated log rows written by many earlier builds.
/// </summary>
/// <remarks>
/// Skipped unless the database path is supplied through the <c>KAEO_VERIFY_DB</c> environment
/// variable, because it depends on a local file that CI and other machines do not have. Run it with:
/// <code>
/// $env:KAEO_VERIFY_DB = "C:\path\to\kaeo_llm_proxy.db"
/// dotnet test --filter RealDatabase_OpensAndReadsThroughTheReconciledSchema
/// </code>
/// It is read-mostly: the only write is a save of the settings row it just read, which is
/// idempotent, and it never touches log rows.
/// </remarks>
public class RealDatabaseVerificationTests
{
    private static string? ConfiguredPath =>
        Environment.GetEnvironmentVariable("KAEO_VERIFY_DB");

    [RequiresRealDatabaseFact]
    public void RealDatabase_OpensAndReadsThroughTheReconciledSchema()
    {
        string? path = ConfiguredPath;

        // The attribute skips visibly when the variable is unset. A set-but-missing path is a
        // misconfigured explicit request for this verification, so it fails loudly instead of
        // silently "passing" without checking anything.
        Assert.True(File.Exists(path), $"KAEO_VERIFY_DB points to '{path}', which does not exist.");

        using AppDatabase database = new(new LoggingSettings { ApplicationDatabasePath = path! });

        // The crash this guards: LoadRuntimeSettings names copilot_compaction_model_name, which a
        // database predating that column does not have.
        RuntimeSettings runtime = database.LoadRuntimeSettings();
        Assert.NotNull(runtime);

        // Every other reader that names baseline columns must also survive the real file.
        Assert.NotNull(database.LoadModelMappings());
        Assert.NotNull(database.LoadInstructionSets());
        Assert.NotNull(database.LoadCredentials());
        Assert.NotNull(database.LoadRecent(10));
        Assert.NotNull(database.LoadRecent(10, LogSource.Mcp));

        // Settings round-trip on the real file, proving the write path against the reconciled schema.
        database.SaveRuntimeSettings(runtime);
        RuntimeSettings reloaded = database.LoadRuntimeSettings();
        Assert.Equal(runtime.EnableCopilotCompactionRouting, reloaded.EnableCopilotCompactionRouting);
        Assert.Equal(runtime.CopilotCompactionModelName, reloaded.CopilotCompactionModelName);
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless <c>KAEO_VERIFY_DB</c> is set, so the test
/// run reports the opt-in database check as skipped rather than as a silent no-op pass.
/// </summary>
internal sealed class RequiresRealDatabaseFactAttribute : FactAttribute
{
    public RequiresRealDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KAEO_VERIFY_DB")))
        {
            Skip = "KAEO_VERIFY_DB is not set; this opt-in check verifies against the developer's "
                + "real database file and is skipped.";
        }
    }
}