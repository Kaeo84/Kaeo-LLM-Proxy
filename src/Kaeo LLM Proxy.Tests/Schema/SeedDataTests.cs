using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Tests for the developer seed data written into a brand-new application database. Two distinct
/// concerns are pinned here: that the seeded configuration matches what the developer actually uses,
/// and that the seeder stays out of the way of any database that already has configuration.
/// </summary>
public class SeedDataTests
{
    private static readonly string[] ExpectedMappingNames =
    [
        "Desktop - Qwen",
        "QC Qwen 3.7 Plus",
        "QC Qwen 3.8 Flash",
        "QC Qwen 3.8 Max",
        "Svr Qwen3 Embedding 4B",
    ];

    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left
    /// behind in the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-seed-{Guid.NewGuid():N}.db");

    private static AppDatabase OpenDatabase(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    private static List<ModelMapping> OpenAndLoad(Action<AppDatabase>? configure = null)
    {
        string path = NewDatabasePath();

        if (configure is not null)
        {
            using AppDatabase setup = OpenDatabase(path);
            configure(setup);
        }

        using AppDatabase database = OpenDatabase(path);
        return [.. database.LoadModelMappings()];
    }

    [Fact]
    public void FreshDatabaseSeedsEveryDefaultMapping()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        Assert.Equal(
            ExpectedMappingNames,
            mappings.Select(m => m.ProxyName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FreshDatabaseSeedsQwenCloudCredential()
    {
        using AppDatabase database = OpenDatabase(NewDatabasePath());

        Assert.Equal(
            [SeedData.QwenCloudCredentialName],
            database.LoadCredentials().Select(c => c.Name));
    }

    [Fact]
    public void SeededMappingsUseQwenThinkingCompatibilityAndKeepAlive()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        var configuration = mappings
            .Select(m => (m.IsEnabled, m.EnableSseKeepAlive, m.EnableThinkingCompatibility, m.ThinkingMode))
            .Distinct()
            .ToList();

        Assert.Equal(
            [(true, true, true, ThinkingMode.QwenThinkingCompatible)],
            configuration);
    }

    [Fact]
    public void SeededMappingsInjectMediumEffortUnderProxyPriority()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        Assert.Equal(
            [(SamplingPriority.Proxy, "medium", "medium")],
            mappings
                .Select(m => (m.ReasoningEffortPriority, m.ReasoningEffort, string.Join(",", m.ReasoningEffortValues)))
                .Distinct());
    }

    [Fact]
    public void SeededMappingsEmitEveryNonLegacyEffortFormat()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        ReasoningEffortFormat expected = ReasoningEffortFormat.Modern
            | ReasoningEffortFormat.QwenCloud
            | ReasoningEffortFormat.ChatTemplateKwargs;

        Assert.Equal([expected], mappings.Select(m => m.ReasoningEffortFormat).Distinct());
    }

    [Fact]
    public void SeededMappingsAdvertiseChatReasoningVisionAndToolCalling()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        Assert.Equal(
            ["chat,reasoning,vision,function_calling,text"],
            mappings.Select(m => string.Join(",", m.Capabilities)).Distinct());
    }

    [Fact]
    public void SeededMappingsLeaveRedactionDisabled()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        Assert.Equal(
            [(false, false, false)],
            mappings
                .Select(m => (m.RedactRequestBodies, m.RedactResponseBodies, m.RedactSensitiveJsonFields))
                .Distinct());
    }

    [Fact]
    public void SeededMappingsCarryTheConfiguredUpstreamsModelsAndContextWindows()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        string qwenCloudUrl = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1";

        var expected = new (string ProxyName, string UpstreamUrl, string ModelName, int ContextWindowTokens, string? CredentialName)[]
        {
            ("Desktop - Qwen", "http://192.168.101.199:8080",
                @"..\Model\Qwen3.8-Unsloth-NVFP4\Qwen3.8-27B-NVFP4-MTP-VERY-HIGH.gguf", 256_000, null),
            ("QC Qwen 3.7 Plus", qwenCloudUrl, "qwen3.7-plus", 1_000_000, SeedData.QwenCloudCredentialName),
            ("QC Qwen 3.8 Flash", qwenCloudUrl, "qwen3.8-flash", 1_000_000, SeedData.QwenCloudCredentialName),
            ("QC Qwen 3.8 Max", qwenCloudUrl, "qwen3.8-max", 1_000_000, SeedData.QwenCloudCredentialName),
            ("Svr Qwen3 Embedding 4B", "http://127.0.0.1:8081",
                @"..\Model\hugger7326-Qwen3-Embedding-4B-Q8_0-GGUF\Qwen3-Embedding-4B-Q8_0.gguf", 2_048, null),
        };

        var actual = mappings
            .Select(m => (m.ProxyName, m.UpstreamUrl, m.ModelName, m.ContextWindowTokens, m.CredentialName))
            .OrderBy(t => t.ProxyName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.OrderBy(t => t.ProxyName, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void SeededMappingsGetDistinctIdentifiers()
    {
        List<ModelMapping> mappings = OpenAndLoad();

        Assert.Equal(mappings.Count, mappings.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void ReopeningASeededDatabaseDoesNotDuplicateMappings()
    {
        string path = NewDatabasePath();

        using (AppDatabase first = OpenDatabase(path))
        {
            Assert.Equal(5, first.LoadModelMappings().Count);
        }

        using AppDatabase second = OpenDatabase(path);

        Assert.Equal(5, second.LoadModelMappings().Count);
    }

    [Fact]
    public void DatabaseWithExistingMappingsIsNotSeeded()
    {
        ModelMapping custom = new() { ProxyName = "mine", ModelName = "mine-upstream", UpstreamUrl = "http://localhost:8080" };
        custom.EnsureId();

        List<ModelMapping> mappings = OpenAndLoad(database => database.SaveModelMappings([custom]));

        Assert.Equal(["mine"], mappings.Select(m => m.ProxyName));
    }

    [Fact]
    public void SeedingDoesNotOverwriteAnExistingCredentialOfTheSameName()
    {
        string path = NewDatabasePath();
        string userToken = "real-user-token";

        using (AppDatabase setup = OpenDatabase(path))
        {
            // Empty the mappings so the seeder runs again, but leave the user's real token in place.
            setup.SaveModelMappings([]);
            setup.SaveCredentials(
            [
                new StoredCredential { Name = SeedData.QwenCloudCredentialName, Secret = userToken },
            ]);
        }

        using AppDatabase reopened = OpenDatabase(path);

        Assert.Equal(
            [userToken],
            reopened.LoadCredentials().Select(c => c.Secret));
    }
}
