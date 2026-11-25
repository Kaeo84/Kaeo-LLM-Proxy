using System.IO.Compression;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Modules;

/// <summary>
/// Phase 6 packaging tests: .kaeomod writer/reader round-trips, ECDSA P-256 signing and trust,
/// tamper detection (payload, manifest, post-install), zip-slip rejection, manifest coverage,
/// and the three ModuleLoadPolicy modes at the single verification seam.
/// </summary>
public sealed class ModulePackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kaeo-pack-{Guid.NewGuid():N}");

    public ModulePackagingTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp artifacts.
        }
    }

    [Fact]
    public void UnsignedPackage_RoundTrips_AndExtractsToIdVersionDirectory()
    {
        string source = CreateSourceDirectory();
        string packagePath = Path.Combine(_root, "unsigned.kaeomod");
        ModulePackageWriter.CreatePackage(source, packagePath, Identity());

        ModulePackageReader.ValidatedPackage validated = ModulePackageReader.ReadAndValidate(packagePath, []);
        Assert.Equal(ModuleSignatureStates.Unsigned, validated.SignatureState);
        Assert.Null(validated.SignerKeyId);
        Assert.Equal("test-module", validated.Manifest.Id);
        Assert.Equal(2, validated.Manifest.Files.Count);

        string dataDirectory = Path.Combine(_root, "data");
        ModulePackageLoader loader = new(new AppSettings(), dataDirectory);
        ModulePackageLoader.PreparedPackage prepared = loader.PreparePackage(packagePath);

        string expectedDir = Path.Combine(dataDirectory, "Modules", "test-module", "1.2.3");
        Assert.Equal(Path.Combine(expectedDir, "FakeModule.dll"), prepared.EntryAssemblyPath);
        Assert.True(File.Exists(prepared.EntryAssemblyPath));
        Assert.True(File.Exists(Path.Combine(expectedDir, "assets", "data.txt")));
        Assert.True(File.Exists(Path.Combine(expectedDir, ModulePackageReader.ManifestEntryName)));
        Assert.Equal(ModuleSignatureStates.Unsigned, prepared.SignatureState);
    }

    [Fact]
    public void SignedPackage_VerifiesAgainstTrustedKey_AndReportsKeyId()
    {
        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        string packagePath = CreateSignedPackage(key);

        ModulePackageReader.ValidatedPackage validated = ModulePackageReader.ReadAndValidate(packagePath, [Trust(key)]);
        Assert.Equal(ModuleSignatureStates.Signed, validated.SignatureState);
        Assert.Equal(key.KeyId, validated.SignerKeyId);
    }

    [Fact]
    public void TamperedPayload_FailsHashCheck()
    {
        string packagePath = CreateUnsignedPackage();
        ReplaceZipEntry(packagePath, "FakeModule.dll", "tampered payload"u8.ToArray());

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, []));
        Assert.Contains("hash check", ex.Message);
    }

    [Fact]
    public void WrongKeySignature_IsRejected_EvenUnderAllowUnsigned()
    {
        ModuleSigning.GeneratedKey signer = ModuleSigning.GenerateKey();
        ModuleSigning.GeneratedKey other = ModuleSigning.GenerateKey();
        string packagePath = CreateSignedPackage(signer);

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, [Trust(other)]));
        Assert.Contains("untrusted", ex.Message);
    }

    [Fact]
    public void TamperedManifest_InvalidatesSignature()
    {
        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        string packagePath = CreateSignedPackage(key);

        byte[] manifestBytes;
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            manifestBytes = ReadEntry(archive, ModulePackageReader.ManifestEntryName);
        ModulePackageManifest manifest = ModulePackageManifest.Deserialize(manifestBytes);
        manifest.Version = "9.9.9";
        ReplaceZipEntry(packagePath, ModulePackageReader.ManifestEntryName, manifest.Serialize());

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, [Trust(key)]));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void ZipSlipEntry_IsRejected()
    {
        string packagePath = Path.Combine(_root, "zipslip.kaeomod");
        ModulePackageManifest manifest = new()
        {
            Id = "test-module",
            Version = "1.2.3",
            EntryAssembly = "FakeModule.dll",
            Files = new Dictionary<string, string> { ["FakeModule.dll"] = new string('0', 64) },
        };
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, ModulePackageReader.ManifestEntryName, manifest.Serialize());
            WriteEntry(archive, "../evil.txt", "escape"u8.ToArray());
        }

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, []));
        Assert.Contains("unsafe path", ex.Message);
    }

    [Fact]
    public void UnlistedPayloadFile_IsRejected()
    {
        string packagePath = CreateUnsignedPackage();
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            WriteEntry(archive, "stowaway.txt", "not in manifest"u8.ToArray());

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, []));
        Assert.Contains("not listed in the manifest", ex.Message);
    }

    [Fact]
    public void PhantomManifestHash_IsRejected()
    {
        string packagePath = CreateUnsignedPackage();
        byte[] manifestBytes;
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            manifestBytes = ReadEntry(archive, ModulePackageReader.ManifestEntryName);
        ModulePackageManifest manifest = ModulePackageManifest.Deserialize(manifestBytes);
        manifest.Files["ghost.dll"] = new string('0', 64);
        ReplaceZipEntry(packagePath, ModulePackageReader.ManifestEntryName, manifest.Serialize());

        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => ModulePackageReader.ReadAndValidate(packagePath, []));
        Assert.Contains("does not contain it", ex.Message);
    }

    [Fact]
    public void RequireSigned_RefusesLooseAssembly_AndUnsignedPackage()
    {
        AppSettings settings = new() { ModuleLoadPolicy = ModuleLoadPolicy.RequireSigned };
        ModulePackageLoader loader = new(settings, Path.Combine(_root, "data"));

        string looseDll = Path.Combine(CreateSourceDirectory(), "FakeModule.dll");
        ModulePackageException looseEx = Assert.Throws<ModulePackageException>(() => loader.CheckLooseAssembly(looseDll));
        Assert.Contains("RequireSigned", looseEx.Message);

        string packagePath = CreateUnsignedPackage();
        ModulePackageException packEx = Assert.Throws<ModulePackageException>(() => loader.PreparePackage(packagePath));
        Assert.Contains("RequireSigned", packEx.Message);
    }

    [Fact]
    public void AllowUnsigned_AndWarn_PermitUnsignedContent()
    {
        string looseDll = Path.Combine(CreateSourceDirectory(), "FakeModule.dll");
        string packagePath = CreateUnsignedPackage();

        foreach (ModuleLoadPolicy policy in new[] { ModuleLoadPolicy.AllowUnsigned, ModuleLoadPolicy.Warn })
        {
            ModulePackageLoader loader = new(new AppSettings { ModuleLoadPolicy = policy }, Path.Combine(_root, "data"));
            loader.CheckLooseAssembly(looseDll);
            ModulePackageLoader.PreparedPackage prepared = loader.PreparePackage(packagePath);
            Assert.Equal(ModuleSignatureStates.Unsigned, prepared.SignatureState);
        }
    }

    [Fact]
    public void RequireSigned_PermitsTrustedSignedPackage()
    {
        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        string packagePath = CreateSignedPackage(key);
        AppSettings settings = new() { ModuleLoadPolicy = ModuleLoadPolicy.RequireSigned };
        settings.TrustedModuleKeys.Add(Trust(key));

        ModulePackageLoader loader = new(settings, Path.Combine(_root, "data"));
        ModulePackageLoader.PreparedPackage prepared = loader.PreparePackage(packagePath);
        Assert.Equal(ModuleSignatureStates.Signed, prepared.SignatureState);
        Assert.Equal(key.KeyId, prepared.SignerKeyId);
    }

    [Fact]
    public void PostInstallTamper_IsDetectedOnReload()
    {
        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        string packagePath = CreateSignedPackage(key);
        AppSettings settings = new();
        settings.TrustedModuleKeys.Add(Trust(key));
        ModulePackageLoader loader = new(settings, Path.Combine(_root, "data"));

        ModulePackageLoader.PreparedPackage prepared = loader.PreparePackage(packagePath);
        (string state, string? keyId) = loader.VerifyRegisteredAssembly(prepared.EntryAssemblyPath);
        Assert.Equal(ModuleSignatureStates.Signed, state);
        Assert.Equal(key.KeyId, keyId);

        File.WriteAllText(prepared.EntryAssemblyPath, "modified after install");
        ModulePackageException ex = Assert.Throws<ModulePackageException>(
            () => loader.VerifyRegisteredAssembly(prepared.EntryAssemblyPath));
        Assert.Contains("modified after install", ex.Message);
    }

    [Fact]
    public void Writer_OverwritesExistingPackage_OnRebuild()
    {
        string source = CreateSourceDirectory();
        string packagePath = Path.Combine(_root, "rebuild.kaeomod");
        ModulePackageWriter.CreatePackage(source, packagePath, Identity());
        ModulePackageWriter.CreatePackage(source, packagePath, Identity());

        ModulePackageReader.ValidatedPackage validated = ModulePackageReader.ReadAndValidate(packagePath, []);
        Assert.Equal("test-module", validated.Manifest.Id);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static ModulePackageWriter.PackageIdentity Identity() =>
        new("test-module", "1.2.3", "FakeModule.dll", Tfm: "net10.0");

    private static TrustedModuleKey Trust(ModuleSigning.GeneratedKey key) => new()
    {
        Name = "test",
        PublicKey = ModuleSigning.PemToBase64Der(key.PublicKeyPem),
    };

    private string CreateSourceDirectory()
    {
        string source = Path.Combine(_root, $"src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(source, "assets"));
        File.WriteAllBytes(Path.Combine(source, "FakeModule.dll"), Encoding.UTF8.GetBytes("fake assembly bytes"));
        File.WriteAllText(Path.Combine(source, "assets", "data.txt"), "payload");
        return source;
    }

    private string CreateUnsignedPackage()
    {
        string packagePath = Path.Combine(_root, $"unsigned-{Guid.NewGuid():N}.kaeomod");
        ModulePackageWriter.CreatePackage(CreateSourceDirectory(), packagePath, Identity());
        return packagePath;
    }

    private string CreateSignedPackage(ModuleSigning.GeneratedKey key)
    {
        string packagePath = Path.Combine(_root, $"signed-{Guid.NewGuid():N}.kaeomod");
        ModulePackageWriter.CreatePackage(CreateSourceDirectory(), packagePath, Identity(), key.PrivateKeyPem);
        return packagePath;
    }

    private static void ReplaceZipEntry(string packagePath, string entryName, byte[] bytes)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        WriteEntry(archive, entryName, bytes);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using Stream stream = archive.GetEntry(name)!.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}