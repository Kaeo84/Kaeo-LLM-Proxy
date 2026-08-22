namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// The full plaintext material of one stored credential. Only the fields the user filled in
/// are populated; a credential may carry just a secret (e.g. an API key or SSH password), a
/// username plus secret, or SSH key/certificate material.
/// </summary>
/// <param name="Name">Unique credential name.</param>
/// <param name="Username">Optional username (e.g. an SSH or service account user).</param>
/// <param name="Secret">Optional secret such as a password or bearer/API key.</param>
/// <param name="PrivateKey">Optional SSH private key (PEM or OpenSSH format).</param>
/// <param name="Certificate">Optional SSH certificate paired with the private key.</param>
public sealed record CredentialMaterial(
    string Name,
    string? Username,
    string? Secret,
    string? PrivateKey,
    string? Certificate);

/// <summary>
/// Read-only access to the host's central credential store. Modules never see encrypted
/// storage or perform any cryptography themselves; the host decrypts on demand.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Names of all stored credentials (never the secrets themselves).</summary>
    IReadOnlyList<string> ListCredentialNames();

    /// <summary>
    /// Resolves the plaintext secret for <paramref name="credentialName"/>, or null when no
    /// such credential exists or decryption fails.
    /// </summary>
    string? ResolveSecret(string credentialName);

    /// <summary>
    /// Resolves the full plaintext material (username, secret, private key, certificate) for
    /// <paramref name="credentialName"/>, or null when no such credential exists.
    /// </summary>
    CredentialMaterial? ResolveCredential(string credentialName);
}
