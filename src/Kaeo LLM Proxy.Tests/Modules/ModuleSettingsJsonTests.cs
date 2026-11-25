using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Module.ChatInterface;
using Kaeo.LlmProxy.Module.CodeVector;
using Kaeo.LlmProxy.Module.Ssh;
using Kaeo.LlmProxy.Module.WebSearch;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Modules;

/// <summary>
/// Exercises the <see cref="IModuleSettingsJson"/> surface every module exposes to the web
/// dashboard: camelCase round-trips, persistence to the shared database, and rejection of
/// out-of-range values and malformed JSON without touching the stored settings.
/// </summary>
public class ModuleSettingsJsonTests
{
    [Fact]
    public void WebSearch_RoundTrip_PersistsAcrossInstances()
    {
        using var fixture = new Fixture();
        AssertRoundTrip(fixture, () => new WebSearchModule(), "maxResults", 7);
    }

    [Fact]
    public void WebSearch_OutOfRange_RejectedWithoutSideEffects()
    {
        using var fixture = new Fixture();
        AssertRejects(fixture, () => new WebSearchModule(), "maxResults", 0);
    }

    [Fact]
    public void Ssh_RoundTrip_PersistsAcrossInstances()
    {
        using var fixture = new Fixture();
        AssertRoundTrip(fixture, () => new SshModule(), "commandTimeoutSeconds", 90);
    }

    [Fact]
    public void Ssh_OutOfRange_RejectedWithoutSideEffects()
    {
        using var fixture = new Fixture();
        AssertRejects(fixture, () => new SshModule(), "commandTimeoutSeconds", 1);
    }

    [Fact]
    public void ChatInterface_RoundTrip_PersistsAcrossInstances()
    {
        using var fixture = new Fixture();
        AssertRoundTrip(fixture, () => new ChatInterfaceModule(), "defaultPollIntervalSeconds", 30);
    }

    [Fact]
    public void ChatInterface_OutOfRange_RejectedWithoutSideEffects()
    {
        using var fixture = new Fixture();
        AssertRejects(fixture, () => new ChatInterfaceModule(), "defaultPollIntervalSeconds", 1);
    }

    [Fact]
    public void CodeVector_RoundTrip_PersistsAcrossInstances()
    {
        using var fixture = new Fixture();
        AssertRoundTrip(fixture, () => new CodeVectorModule(), "defaultTopK", 12);
    }

    [Fact]
    public void CodeVector_OutOfRange_RejectedWithoutSideEffects()
    {
        using var fixture = new Fixture();
        AssertRejects(fixture, () => new CodeVectorModule(), "defaultTopK", 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Apply_MalformedOrEmptyJson_Throws(string json)
    {
        using var fixture = new Fixture();
        var module = new WebSearchModule();
        module.Initialize(fixture.Context);
        IModuleSettingsJson surface = module;

        string before = surface.GetSettingsJson();
        Assert.ThrowsAny<Exception>(() => surface.ApplySettingsJson(json));
        Assert.Equal(before, surface.GetSettingsJson());
    }

    /// <summary>
    /// Applies a changed value through the JSON surface, verifies the change is visible
    /// immediately, and verifies a freshly initialized module reads the persisted value.
    /// </summary>
    private static void AssertRoundTrip<TModule>(Fixture fixture, Func<TModule> factory, string propertyName, int newValue)
        where TModule : IKaeoModule, IModuleSettingsJson
    {
        TModule module = factory();
        module.Initialize(fixture.Context);
        IModuleSettingsJson surface = module;

        JsonNode node = JsonNode.Parse(surface.GetSettingsJson())!;
        Assert.NotNull(node[propertyName]);
        node[propertyName] = newValue;
        surface.ApplySettingsJson(node.ToJsonString());

        using (JsonDocument after = JsonDocument.Parse(surface.GetSettingsJson()))
            Assert.Equal(newValue, after.RootElement.GetProperty(propertyName).GetInt32());

        TModule reloaded = factory();
        reloaded.Initialize(fixture.Context);
        using JsonDocument persisted = JsonDocument.Parse(((IModuleSettingsJson)reloaded).GetSettingsJson());
        Assert.Equal(newValue, persisted.RootElement.GetProperty(propertyName).GetInt32());
    }

    /// <summary>
    /// An out-of-range value must throw before anything is persisted: the stored settings
    /// read back byte-for-byte unchanged.
    /// </summary>
    private static void AssertRejects<TModule>(Fixture fixture, Func<TModule> factory, string propertyName, int invalidValue)
        where TModule : IKaeoModule, IModuleSettingsJson
    {
        TModule module = factory();
        module.Initialize(fixture.Context);
        IModuleSettingsJson surface = module;

        string before = surface.GetSettingsJson();
        JsonNode node = JsonNode.Parse(before)!;
        node[propertyName] = invalidValue;

        Assert.ThrowsAny<ArgumentException>(() => surface.ApplySettingsJson(node.ToJsonString()));
        Assert.Equal(before, surface.GetSettingsJson());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kaeo-modsettings-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var logging = new LoggingSettings
            {
                ApplicationDatabasePath = Path.Combine(_directory, "app.db"),
            };
            Database = new AppDatabase(logging);
            Context = new ModuleContext(
                new ModuleDatabaseGateway(Database),
                new FakeSecrets(),
                new HostInfo("localhost", 11434, apiExplorerEnabled: false, openApiSpecUrl: null),
                new NoopActivityLog(),
                _directory);
        }

        public AppDatabase Database { get; }

        public ModuleContext Context { get; }

        public void Dispose()
        {
            Database.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp artifacts.
            }
        }
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public IReadOnlyList<string> ListCredentialNames() => [];
        public string? ResolveSecret(string credentialName) => null;
        public CredentialMaterial? ResolveCredential(string credentialName) => null;
    }

    private sealed class NoopActivityLog : IMcpActivityLog
    {
        public void Write(McpActivityEntry entry)
        {
        }
    }
}