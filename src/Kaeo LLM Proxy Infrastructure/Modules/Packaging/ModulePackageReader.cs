using System.IO.Compression;
using System.Security.Cryptography;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Reads and verifies <c>.kaeomod</c> packages. All checks (size caps, zip-slip, manifest
/// coverage, payload hashes, signature trust) run before anything is extracted to disk.
/// </summary>
internal static class ModulePackageReader
{
    public const string ManifestEntryName = "manifest.json";
    public const string SignatureEntryName = "manifest.sig";
    public const string PackageExtension = ".kaeomod";

    public const long MaxPackageBytes = 256L * 1024 * 1024;
    public const int MaxEntryCount = 4096;
    public const long MaxEntryBytes = 128L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 512L * 1024 * 1024;

    /// <summary>A fully verified package, ready to extract.</summary>
    internal sealed record ValidatedPackage(
        string PackagePath,
        ModulePackageManifest Manifest,
        string SignatureState,
        string? SignerKeyId);

    /// <summary>
    /// Validates the package end to end. Throws <see cref="ModulePackageException"/> with a
    /// user-presentable message on any failure. Policy (AllowUnsigned/Warn/RequireSigned) is
    /// applied by <see cref="ModulePackageLoader"/>, not here; an invalid or untrusted signature
    /// is always rejected regardless of policy.
    /// </summary>
    public static ValidatedPackage ReadAndValidate(string packagePath, IReadOnlyList<TrustedModuleKey> trustedKeys)
    {
        FileInfo info = new(packagePath);
        if (!info.Exists)
            throw new ModulePackageException($"The module package '{packagePath}' does not exist.");
        if (info.Length > MaxPackageBytes)
            throw new ModulePackageException($"The module package exceeds the {MaxPackageBytes / (1024 * 1024)} MB size cap.");

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(packagePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ModulePackageException($"The module package is not a readable zip archive: {ex.Message}", ex);
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntryCount)
                throw new ModulePackageException($"The module package exceeds the {MaxEntryCount} entry cap.");

            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
            long totalUncompressed = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ValidateEntryName(entry.FullName);
                if (entry.FullName.EndsWith('/'))
                    continue; // directory marker
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new ModulePackageException($"The module package contains a duplicate entry '{entry.FullName}'.");
                if (entry.Length > MaxEntryBytes)
                    throw new ModulePackageException($"Package entry '{entry.FullName}' exceeds the {MaxEntryBytes / (1024 * 1024)} MB entry cap.");
                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxTotalUncompressedBytes)
                    throw new ModulePackageException($"The module package exceeds the {MaxTotalUncompressedBytes / (1024 * 1024)} MB uncompressed cap.");
            }

            if (!entries.TryGetValue(ManifestEntryName, out ZipArchiveEntry? manifestEntry))
                throw new ModulePackageException("The module package has no manifest.json at its root.");

            byte[] manifestBytes = ReadEntryBytes(manifestEntry);
            ModulePackageManifest manifest = ModulePackageManifest.Deserialize(manifestBytes);
            ValidateManifest(manifest);

            // The manifest must cover exactly the payload set: no unhashed files, no phantom hashes.
            foreach (string name in entries.Keys)
            {
                if (name is ManifestEntryName or SignatureEntryName)
                    continue;
                if (!manifest.Files.ContainsKey(name))
                    throw new ModulePackageException($"Package file '{name}' is not listed in the manifest.");
            }

            foreach ((string name, string expectedHash) in manifest.Files)
            {
                if (!entries.TryGetValue(name, out ZipArchiveEntry? entry))
                    throw new ModulePackageException($"The manifest lists '{name}' but the package does not contain it.");
                string actualHash = HashEntry(entry);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ModulePackageException($"Package file '{name}' failed its SHA-256 hash check (payload tampered or corrupt).");
            }

            string signatureState = ModuleSignatureStates.Unsigned;
            string? signerKeyId = null;
            if (entries.TryGetValue(SignatureEntryName, out ZipArchiveEntry? signatureEntry))
            {
                byte[] signature = ReadEntryBytes(signatureEntry);
                TrustedModuleKey? signer = ModuleSigning.FindTrustedSigner(manifestBytes, signature, trustedKeys);
                if (signer is null)
                    throw new ModulePackageException("The package signature is invalid or was made with an untrusted key.");
                signatureState = ModuleSignatureStates.Signed;
                signerKeyId = ModuleSigning.KeyIdFromPublicKeyDer(Convert.FromBase64String(signer.PublicKey));
            }

            return new ValidatedPackage(packagePath, manifest, signatureState, signerKeyId);
        }
    }

    /// <summary>
    /// Extracts a validated package into <paramref name="targetDirectory"/> (overwriting an
    /// existing copy of the same id/version). Names were validated in ReadAndValidate; the
    /// full-path containment check is a second zip-slip guard.
    /// </summary>
    public static void Extract(ValidatedPackage package, string targetDirectory)
    {
        string targetFull = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetFull);

        using ZipArchive archive = ZipFile.OpenRead(package.PackagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            string destination = Path.GetFullPath(Path.Combine(targetFull, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ModulePackageException($"Package entry '{entry.FullName}' would extract outside the module directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Re-verifies an already-extracted package on disk (startup reload path): manifest hashes
    /// against the on-disk files and, when present, the detached signature against the trust list.
    /// </summary>
    public static (string SignatureState, string? SignerKeyId) VerifyExtractedDirectory(
        string directory, IReadOnlyList<TrustedModuleKey> trustedKeys)
    {
        string manifestPath = Path.Combine(directory, ManifestEntryName);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        ModulePackageManifest manifest = ModulePackageManifest.Deserialize(manifestBytes);
        ValidateManifest(manifest);

        foreach ((string name, string expectedHash) in manifest.Files)
        {
            string filePath = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
                throw new ModulePackageException($"Extracted module file '{name}' is missing.");
            string actualHash;
            using (FileStream stream = File.OpenRead(filePath))
                actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ModulePackageException($"Extracted module file '{name}' failed its SHA-256 hash check (modified after install).");
        }

        string signaturePath = Path.Combine(directory, SignatureEntryName);
        if (!File.Exists(signaturePath))
            return (ModuleSignatureStates.Unsigned, null);

        byte[] signature = File.ReadAllBytes(signaturePath);
        TrustedModuleKey? signer = ModuleSigning.FindTrustedSigner(manifestBytes, signature, trustedKeys);
        if (signer is null)
            throw new ModulePackageException("The extracted module's signature is invalid or was made with an untrusted key.");
        return (ModuleSignatureStates.Signed, ModuleSigning.KeyIdFromPublicKeyDer(Convert.FromBase64String(signer.PublicKey)));
    }

    private static void ValidateManifest(ModulePackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new ModulePackageException("The package manifest has no id.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new ModulePackageException("The package manifest has no version.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new ModulePackageException("The package manifest has no entryAssembly.");
        ValidatePathSegment(manifest.Id, "manifest id");
        ValidatePathSegment(manifest.Version, "manifest version");
        ValidateEntryName(manifest.EntryAssembly);
        if (manifest.Files.Count == 0)
            throw new ModulePackageException("The package manifest lists no files.");
        if (!manifest.Files.ContainsKey(manifest.EntryAssembly))
            throw new ModulePackageException($"The entry assembly '{manifest.EntryAssembly}' is not listed in the manifest files.");
        foreach (string name in manifest.Files.Keys)
            ValidateEntryName(name);

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            if (!Version.TryParse(manifest.MinHostVersion, out Version? required))
                throw new ModulePackageException($"The manifest minHostVersion '{manifest.MinHostVersion}' is not a valid version.");
            Version hostVersion = typeof(ModulePackageReader).Assembly.GetName().Version ?? new Version(0, 0);
            if (hostVersion < required)
                throw new ModulePackageException($"The package requires host version {required} but this host is {hostVersion}.");
        }
    }

    /// <summary>Rejects rooted paths, traversal, drive letters and backslashes (zip-slip).</summary>
    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ModulePackageException("The package contains an entry with an empty name.");
        if (name.Contains('\\') || Path.IsPathRooted(name) || name.Contains(':'))
            throw new ModulePackageException($"Package entry '{name}' has an unsafe path.");
        foreach (string segment in name.Split('/'))
        {
            if (segment is ".." or ".")
                throw new ModulePackageException($"Package entry '{name}' has an unsafe path segment.");
        }
    }

    private static void ValidatePathSegment(string value, string what)
    {
        if (value.Contains('/') || value.Contains('\\') || value.Contains(':') || value.Contains(".."))
            throw new ModulePackageException($"The {what} '{value}' is not a safe path segment.");
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string HashEntry(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}