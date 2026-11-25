using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// The single seam through which every module load passes (loose DLL import, package import,
/// upload, startup reload). Applies <see cref="ModuleLoadPolicy"/> and routes packages through
/// verification + extraction. Invalid/untrusted signatures are always rejected regardless of
/// policy; policy only governs unsigned content.
/// </summary>
internal sealed class ModulePackageLoader
{
    private readonly AppSettings _settings;
    private readonly string _dataDirectory;

    public ModulePackageLoader(AppSettings settings, string dataDirectory)
    {
        _settings = settings;
        _dataDirectory = dataDirectory;
    }

    /// <summary>A verified, extracted package ready for assembly load.</summary>
    internal sealed record PreparedPackage(string EntryAssemblyPath, string SignatureState, string? SignerKeyId);

    public static bool IsPackagePath(string path) =>
        path.EndsWith(ModulePackageReader.PackageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a <c>.kaeomod</c> package, applies policy, and extracts it to
    /// <c>{dataDirectory}/Modules/{id}/{version}/</c>. Returns the extracted entry assembly path.
    /// </summary>
    public PreparedPackage PreparePackage(string packagePath)
    {
        ModulePackageReader.ValidatedPackage validated =
            ModulePackageReader.ReadAndValidate(packagePath, _settings.TrustedModuleKeys);
        ApplyUnsignedPolicy(validated.SignatureState, $"package '{Path.GetFileName(packagePath)}'");

        string targetDirectory = Path.Combine(
            _dataDirectory, "Modules", SanitizeSegment(validated.Manifest.Id), SanitizeSegment(validated.Manifest.Version));
        ModulePackageReader.Extract(validated, targetDirectory);

        string entryPath = Path.Combine(
            targetDirectory, validated.Manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
        return new PreparedPackage(entryPath, validated.SignatureState, validated.SignerKeyId);
    }

    /// <summary>Policy gate for a loose (non-package) assembly about to be imported.</summary>
    public void CheckLooseAssembly(string assemblyPath) =>
        ApplyUnsignedPolicy(ModuleSignatureStates.Unsigned, $"assembly '{Path.GetFileName(assemblyPath)}'");

    /// <summary>
    /// Startup-reload gate for a registered assembly path. When a manifest sits beside the DLL
    /// the extracted package is re-verified (hashes + signature) so post-install tampering is
    /// caught; otherwise the loose-assembly policy applies.
    /// </summary>
    public (string SignatureState, string? SignerKeyId) VerifyRegisteredAssembly(string assemblyPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        if (!File.Exists(Path.Combine(directory, ModulePackageReader.ManifestEntryName)))
        {
            CheckLooseAssembly(assemblyPath);
            return (ModuleSignatureStates.Unsigned, null);
        }

        (string state, string? keyId) = ModulePackageReader.VerifyExtractedDirectory(directory, _settings.TrustedModuleKeys);
        ApplyUnsignedPolicy(state, $"installed module at '{directory}'");
        return (state, keyId);
    }

    private void ApplyUnsignedPolicy(string signatureState, string what)
    {
        if (signatureState != ModuleSignatureStates.Unsigned)
            return;

        switch (_settings.ModuleLoadPolicy)
        {
            case ModuleLoadPolicy.RequireSigned:
                throw new ModulePackageException(
                    $"Loading {what} was refused: it is unsigned and ModuleLoadPolicy is RequireSigned.");
            case ModuleLoadPolicy.Warn:
                Log.Warning("Loading unsigned module {What} (ModuleLoadPolicy=Warn).", what);
                break;
        }
    }

    private static string SanitizeSegment(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }
        string result = new(buffer);
        return result.Replace("..", "__");
    }
}