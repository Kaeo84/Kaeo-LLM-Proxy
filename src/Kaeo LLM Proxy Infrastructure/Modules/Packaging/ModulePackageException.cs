namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// A module package failed verification (bad manifest, hash mismatch, untrusted or invalid
/// signature, zip-slip entry, size cap exceeded, or policy refusal). The message is safe to
/// show to the user and to return from the dashboard upload endpoint.
/// </summary>
internal sealed class ModulePackageException : Exception
{
    public ModulePackageException(string message) : base(message)
    {
    }

    public ModulePackageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}