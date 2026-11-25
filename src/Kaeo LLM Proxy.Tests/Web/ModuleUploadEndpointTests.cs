using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Dashboard upload endpoint tests (Phase 6): multipart .kaeomod upload → verify → extract →
/// load round-trip with a real module assembly, policy enforcement, and malformed-upload
/// rejection. Real Kestrel on ephemeral ports, mirroring WebDashboardEndpointTests.
/// </summary>
[Collection("AppLogger")]
public sealed class ModuleUploadEndpointTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a loaded module DLL may still be mapped.
            }
        }
    }

    [Fact]
    public async Task Upload_SignedPackage_ImportsAndLoads()
    {
        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        await using Bundle b = await CreateBundleAsync(settings =>
            settings.TrustedModuleKeys.Add(new TrustedModuleKey
            {
                Name = "test",
                PublicKey = ModuleSigning.PemToBase64Der(key.PublicKeyPem),
            }));

        string packagePath = CreateRealModulePackage(key.PrivateKeyPem);
        HttpResponseMessage response = await UploadAsync(b.Client, packagePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ModuleSignatureStates.Signed, doc.RootElement.GetProperty("signatureState").GetString());
        Assert.Equal(key.KeyId, doc.RootElement.GetProperty("signerKeyId").GetString());
        int id = doc.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        ModuleRegistryEntry entry = Assert.Single(b.Database.LoadModuleRegistry(), e => e.Id == id);
        Assert.Equal(ModuleSignatureStates.Signed, entry.SignatureState);
        Assert.Equal(key.KeyId, entry.SignerKeyId);
        Assert.Contains(Path.Combine("Modules", "web-search-test", "1.0.0"), entry.AssemblyPath);
        Assert.True(File.Exists(entry.AssemblyPath));

        using JsonDocument list = JsonDocument.Parse(await b.Client.GetStringAsync("/api/modules"));
        JsonElement listed = Assert.Single(list.RootElement.EnumerateArray(), e => e.GetProperty("id").GetInt32() == id);
        Assert.Equal(ModuleSignatureStates.Signed, listed.GetProperty("signatureState").GetString());
    }

    [Fact]
    public async Task Upload_UnsignedPackage_WhenRequireSigned_IsRefused()
    {
        await using Bundle b = await CreateBundleAsync(settings =>
            settings.ModuleLoadPolicy = ModuleLoadPolicy.RequireSigned);

        string packagePath = CreateRealModulePackage(privateKeyPem: null);
        HttpResponseMessage response = await UploadAsync(b.Client, packagePath);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("RequireSigned", await response.Content.ReadAsStringAsync());
        Assert.Empty(b.Database.LoadModuleRegistry());
    }

    [Fact]
    public async Task Upload_NonMultipart_IsRefused()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Client.PostAsync("/api/modules/upload",
            new StringContent("not a form"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutDashboardSession_IsUnauthorized()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Anonymous.PostAsync("/api/modules/upload",
            new StringContent("not a form"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(b.Database.LoadModuleRegistry());
    }

    [Fact]
    public async Task Upload_CorruptPackage_IsRefused()
    {
        await using Bundle b = await CreateBundleAsync();
        string fakePath = Path.Combine(Path.GetTempPath(), $"kaeo-upload-{Guid.NewGuid():N}.kaeomod");
        await File.WriteAllBytesAsync(fakePath, "this is not a zip"u8.ToArray());
        try
        {
            HttpResponseMessage response = await UploadAsync(b.Client, fakePath);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(b.Database.LoadModuleRegistry());
        }
        finally
        {
            File.Delete(fakePath);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Packages the real WebSearch module DLL (single-file module, no unique runtime deps) from
    /// the test output directory under a distinct id so the import loads it end to end.
    /// </summary>
    private string CreateRealModulePackage(string? privateKeyPem)
    {
        string source = Path.Combine(Path.GetTempPath(), $"kaeo-upload-src-{Guid.NewGuid():N}");
        _tempDirectories.Add(source);
        Directory.CreateDirectory(source);
        const string dllName = "Kaeo LLM Proxy Module Web Search.dll";
        File.Copy(Path.Combine(AppContext.BaseDirectory, dllName), Path.Combine(source, dllName));

        string packagePath = Path.Combine(source, "web-search-test.kaeomod");
        ModulePackageWriter.CreatePackage(
            source, packagePath,
            new ModulePackageWriter.PackageIdentity("web-search-test", "1.0.0", dllName, Tfm: "net10.0"),
            privateKeyPem);
        return packagePath;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string packagePath)
    {
        using MultipartFormDataContent form = new();
        var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(packagePath));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "package", Path.GetFileName(packagePath));
        return await client.PostAsync("/api/modules/upload", form);
    }

    private static async Task<Bundle> CreateBundleAsync(Action<AppSettings>? configureSettings = null)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-upload-{Guid.NewGuid():N}.db");
        var settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            AutoStartProxy = false,
            MaxLogEntries = 100,
        };
        settings.Logging.ApplicationDatabasePath = dbPath;
        configureSettings?.Invoke(settings);

        var database = new AppDatabase(settings.Logging);
        DashboardTestSession.SeedAdmin(database);
        var moduleHost = new ModuleHost(database, settings);
        var controller = new ProxyHostController(settings, database, moduleHost);

        settings.WebDashboard.Enabled = true;
        settings.WebDashboard.Port = GetFreePort();
        settings.WebDashboard.ListenAddress = "localhost";

        var host = new WebDashboardHost(settings, controller);
        await host.StartAsync();

        HttpClient client = DashboardTestSession.CreateClient(host.Url!, useCookies: true, timeout: TimeSpan.FromSeconds(30));
        await DashboardTestSession.SignInAdminAsync(client);

        return new Bundle
        {
            Host = host,
            Controller = controller,
            Database = database,
            Client = client,
            Anonymous = DashboardTestSession.CreateClient(host.Url!, useCookies: false),
            DbPath = dbPath,
            ExtractedModuleDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "Modules", "web-search-test"),
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp artifacts.
        }
    }

    private sealed class Bundle : IAsyncDisposable
    {
        public required WebDashboardHost Host { get; init; }
        public required ProxyHostController Controller { get; init; }
        public required AppDatabase Database { get; init; }
        public required HttpClient Client { get; init; }
        public required HttpClient Anonymous { get; init; }
        public required string DbPath { get; init; }
        public required string ExtractedModuleDir { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Anonymous.Dispose();
            await Host.DisposeAsync();
            await Controller.DisposeAsync();
            Database.Dispose();
            TryDelete(DbPath);
            TryDelete(DbPath + "-shm");
            TryDelete(DbPath + "-wal");
            try
            {
                if (Directory.Exists(ExtractedModuleDir))
                    Directory.Delete(ExtractedModuleDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a loaded module DLL may still be mapped.
            }
        }
    }
}