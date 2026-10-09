

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class MirrorRegistration
{
    /// <summary>Source kind for the mirror: <c>"git"</c> (clone/fetch a remote) or <c>"dir"</c> (watch a local directory/file share).</summary>
    public const string SourceKindGit = "git";
    public const string SourceKindDir = "dir";

    public int Id { get; set; }
    public string CollectionName { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? CredentialName { get; set; }
    public string? MirrorPath { get; set; }
    public string? PathPrefix { get; set; }
    /// <summary>Source kind (<c>"git"</c> or <c>"dir"</c>). Defaults to <c>"git"</c> for existing rows.</summary>
    public string SourceKind { get; set; } = SourceKindGit;
    /// <summary>Local directory or file-share path to watch, used when <see cref="SourceKind"/> is <c>"dir"</c>.</summary>
    public string? SourcePath { get; set; }
    public string? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }

    public bool IsLocalDirectory => string.Equals(SourceKind, SourceKindDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable description of the mirror source, for status/log display.</summary>
    public string DescribeSource => IsLocalDirectory
        ? $"local dir {SourcePath}"
        : $"{RemoteUrl} [{Branch}]";
}
