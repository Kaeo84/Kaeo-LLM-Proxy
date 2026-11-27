using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// The <c>manifest.json</c> inside a <c>.kaeomod</c> package. The manifest signature
/// (<c>manifest.sig</c>) covers the exact manifest bytes; the per-file SHA-256 hashes cover
/// every payload file, so tampering anywhere in the package is detectable end to end.
/// </summary>
internal sealed class ModulePackageManifest
{
    /// <summary>Package identity (used for the extraction directory <c>Modules/{id}/{version}/</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Package version string.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Relative path (zip-style '/') of the assembly containing the IKaeoModule implementation.</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>Target framework the payload was built for (informational).</summary>
    public string? Tfm { get; set; }

    /// <summary>Runtime identifier the payload was built for, when RID-specific (informational).</summary>
    public string? Rid { get; set; }

    /// <summary>Minimum host assembly version required, when set (e.g. "1.0.0.0").</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of every payload file, keyed by its zip-style relative path.
    /// Excludes <c>manifest.json</c> and <c>manifest.sig</c> themselves.
    /// </summary>
    public Dictionary<string, string> Files { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static ModulePackageManifest Deserialize(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ModulePackageManifest>(bytes, JsonOptions)
                ?? throw new ModulePackageException("The package manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new ModulePackageException($"The package manifest is not valid JSON: {ex.Message}", ex);
        }
    }
}