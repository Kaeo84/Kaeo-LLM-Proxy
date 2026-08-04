namespace Kaeo.LlmProxy.Modules;

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
}
