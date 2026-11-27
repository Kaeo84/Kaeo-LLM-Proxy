using System.IO.Compression;
using System.Security.Cryptography;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Builds <c>.kaeomod</c> packages from a module output directory: hashes every payload file
/// into the manifest, optionally signs the manifest with a PKCS#8 PEM key. Used by the headless
/// CLI verbs and the ModuleBuild.targets pack step.
/// </summary>
internal static class ModulePackageWriter
{
    /// <summary>Package identity supplied by the caller (CLI flags / MSBuild properties).</summary>
    internal sealed record PackageIdentity(
        string Id,
        string Version,
        string EntryAssembly,
        string? Tfm = null,
        string? Rid = null,
        string? MinHostVersion = null);

    /// <summary>
    /// Creates a package from <paramref name="sourceDirectory"/>. <paramref name="privateKeyPem"/>
    /// (file content, not path) signs the manifest when provided; otherwise the package is unsigned.
    /// Returns the output package path.
    /// </summary>
    public static string CreatePackage(
        string sourceDirectory, string outputPath, PackageIdentity identity, string? privateKeyPem = null)
    {
        string sourceFull = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceFull))
            throw new ModulePackageException($"The module directory '{sourceDirectory}' does not exist.");

        string entryRelative = NormalizeRelative(identity.EntryAssembly, sourceFull);
        if (!File.Exists(Path.Combine(sourceFull, entryRelative.Replace('/', Path.DirectorySeparatorChar))))
            throw new ModulePackageException($"The entry assembly '{identity.EntryAssembly}' was not found under '{sourceDirectory}'.");

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceFull, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is ModulePackageReader.ManifestEntryName or ModulePackageReader.SignatureEntryName)
                continue;
            if (relative.EndsWith(ModulePackageReader.PackageExtension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (files.Count >= ModulePackageReader.MaxEntryCount)
                throw new ModulePackageException($"The module directory exceeds the {ModulePackageReader.MaxEntryCount} file cap.");
            using FileStream stream = File.OpenRead(file);
            files[relative] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        ModulePackageManifest manifest = new()
        {
            Id = identity.Id,
            Version = identity.Version,
            EntryAssembly = entryRelative,
            Tfm = identity.Tfm,
            Rid = identity.Rid,
            MinHostVersion = identity.MinHostVersion,
            Files = files,
        };
        byte[] manifestBytes = manifest.Serialize();
        byte[]? signature = privateKeyPem is null ? null : ModuleSigning.SignManifest(manifestBytes, privateKeyPem);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        // ZipArchiveMode.Create refuses an existing file; rebuilds must overwrite the old package.
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            WriteBytes(archive, ModulePackageReader.ManifestEntryName, manifestBytes);
            if (signature is not null)
                WriteBytes(archive, ModulePackageReader.SignatureEntryName, signature);
            foreach (string relative in files.Keys)
            {
                string filePath = Path.Combine(sourceFull, relative.Replace('/', Path.DirectorySeparatorChar));
                archive.CreateEntryFromFile(filePath, relative, CompressionLevel.Optimal);
            }
        }

        // Round-trip through the reader so a produced package is guaranteed loadable.
        // A signed package is verified against its own public key here; trust enforcement
        // happens at load time against the configured trust list.
        IReadOnlyList<Kaeo.LlmProxy.Core.Models.TrustedModuleKey> roundTripKeys = [];
        if (privateKeyPem is not null)
        {
            using System.Security.Cryptography.ECDsa ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            roundTripKeys =
            [
                new Kaeo.LlmProxy.Core.Models.TrustedModuleKey
                {
                    PublicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                },
            ];
        }
        ModulePackageReader.ReadAndValidate(outputPath, roundTripKeys);
        return outputPath;
    }

    /// <summary>
    /// Resolves the entry assembly argument (absolute or relative to the source directory) to a
    /// safe zip-style relative path inside the directory.
    /// </summary>
    public static string NormalizeRelative(string entryAssembly, string sourceFull)
    {
        string combined = Path.IsPathRooted(entryAssembly)
            ? entryAssembly
            : Path.Combine(sourceFull, entryAssembly);
        string full = Path.GetFullPath(combined);
        if (!full.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ModulePackageException($"The entry assembly '{entryAssembly}' is outside the module directory.");
        return Path.GetRelativePath(sourceFull, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void WriteBytes(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}