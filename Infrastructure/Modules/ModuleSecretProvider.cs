using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Modules;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Implements <see cref="ISecretProvider"/> over the host's central credential store.
/// Credentials are decrypted once at startup and kept in memory; this adapter only reads
/// those in-memory values and never touches encryption itself.
/// </summary>
internal sealed class ModuleSecretProvider(AppSettings settings) : ISecretProvider
{
    private readonly AppSettings _settings = settings;

    public IReadOnlyList<string> ListCredentialNames() =>
        _settings.Credentials
            .Select(credential => credential.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string? ResolveSecret(string credentialName)
    {
        StoredCredential? credential = _settings.FindCredential(credentialName);
        return credential is not null && !string.IsNullOrWhiteSpace(credential.Secret)
            ? credential.Secret
            : null;
    }
}
