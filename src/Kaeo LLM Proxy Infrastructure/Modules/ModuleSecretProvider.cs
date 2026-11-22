using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Implements <see cref="ISecretProvider"/> over the host's central credential store.
/// Credentials are decrypted once at startup and kept in memory; this adapter only reads
/// those in-memory values and never touches encryption itself.
/// </summary>
internal sealed class ModuleSecretProvider(AppSettings settings) : ISecretProvider
{
    private readonly AppSettings _settings = settings;

    /// <summary>
    /// Snapshot of the credentials list. The host may mutate the live list (settings save)
    /// while a module reads it; copying first avoids collection-modified races.
    /// </summary>
    private StoredCredential[] SnapshotCredentials()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return _settings.Credentials.ToArray();
            }
            catch (ArgumentException)
            {
                // List<T>.ToArray can throw if the list is resized mid-copy; retry once.
            }
        }

        return [];
    }

    private StoredCredential? FindCredential(string credentialName) =>
        SnapshotCredentials().FirstOrDefault(
            credential => string.Equals(credential.Name, credentialName, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ListCredentialNames() =>
        SnapshotCredentials()
            .Select(credential => credential.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string? ResolveSecret(string credentialName)
    {
        StoredCredential? credential = FindCredential(credentialName);
        return credential is not null && !string.IsNullOrWhiteSpace(credential.Secret)
            ? credential.Secret
            : null;
    }

    public CredentialMaterial? ResolveCredential(string credentialName)
    {
        StoredCredential? credential = FindCredential(credentialName);
        if (credential is null)
            return null;

        return new CredentialMaterial(
            credential.Name,
            string.IsNullOrWhiteSpace(credential.Username) ? null : credential.Username,
            string.IsNullOrWhiteSpace(credential.Secret) ? null : credential.Secret,
            string.IsNullOrWhiteSpace(credential.PrivateKey) ? null : credential.PrivateKey,
            string.IsNullOrWhiteSpace(credential.Certificate) ? null : credential.Certificate);
    }
}
