

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class MirrorRegistration
{
    public int Id { get; set; }
    public string CollectionName { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? CredentialName { get; set; }
    public string? MirrorPath { get; set; }
    public string? PathPrefix { get; set; }
    public string? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
}
