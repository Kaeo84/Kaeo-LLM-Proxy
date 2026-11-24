using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Kaeo.LlmProxy.Core.Security;

/// <summary>
/// Persists <see cref="Models.AppSettings.SecurityPassphrase"/> with DPAPI protection at rest
/// (CurrentUser scope). The in-memory value keeps its legacy form — <c>!pwh!</c> + Base64
/// SHA-256 hash, or a legacy raw passphrase — so the derived key material is unchanged and
/// previously encrypted secrets still decrypt. Only the on-disk representation changes to
/// <c>dpapi:</c> + Base64(ProtectedData(hash bytes)). Legacy values are read unchanged and
/// upgraded to DPAPI on the next save; if DPAPI is unavailable the legacy form is stored
/// with a warning instead.
/// </summary>
internal sealed class SecurityPassphraseConverter : JsonConverter<string?>
{
    private const string DpapiPrefix = "dpapi:";

    // Fixed entropy binds the blob to this setting rather than to any DPAPI caller.
    private static readonly byte[] Entropy = "KaeoLlmProxy.SecurityPassphrase"u8.ToArray();

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (string.IsNullOrEmpty(value) || !value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return value; // Legacy "!pwh!" or raw form: use unchanged.

        if (!OperatingSystem.IsWindows())
        {
            Log.Warning("A DPAPI-protected security passphrase cannot be read on this platform; " +
                "the passphrase will be requested at startup.");
            return null;
        }

        try
        {
            byte[] hashBytes = ProtectedData.Unprotect(
                Convert.FromBase64String(value[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return SecretProtector.HashPrefix + Convert.ToBase64String(hashBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            Log.Warning(ex,
                "Could not unprotect the stored security passphrase (different Windows user, roaming profile, " +
                "or corrupted value); the passphrase will be requested at startup.");
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (OperatingSystem.IsWindows() && SecretProtector.IsHashedPassphrase(value))
        {
            try
            {
                byte[] hashBytes = Convert.FromBase64String(SecretProtector.StripHashPrefix(value));
                byte[] protectedBytes = ProtectedData.Protect(hashBytes, Entropy, DataProtectionScope.CurrentUser);
                writer.WriteStringValue(DpapiPrefix + Convert.ToBase64String(protectedBytes));
                return;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
            {
                Log.Warning(ex,
                    "DPAPI protection of the security passphrase failed; storing the legacy hash form instead.");
            }
        }

        writer.WriteStringValue(value);
    }
}