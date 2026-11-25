using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// JSON settings surface for the web dashboard. Applies the same change policy as the WinForms
/// config page: values are copied onto the live settings instance (engines hold that reference),
/// persisted in one transaction, then the affected cached engines are invalidated.
/// </summary>
public sealed partial class CodeVectorModule : IModuleSettingsJson
{
    string IModuleSettingsJson.GetSettingsJson() => ModuleSettingsJson.Serialize(Settings);

    void IModuleSettingsJson.ApplySettingsJson(string json)
    {
        CodeVectorSettings updated = ModuleSettingsJson.Deserialize<CodeVectorSettings>(json);

        ModuleSettingsJson.RequireDefinedEnum(updated.BackendType, nameof(updated.BackendType));
        ModuleSettingsJson.RequireDefinedEnum(updated.McpLogLevel, nameof(updated.McpLogLevel));
        ModuleSettingsJson.RequireRange(updated.RemoteTimeoutSeconds, 1, 3_600, nameof(updated.RemoteTimeoutSeconds));
        ModuleSettingsJson.RequireRange(updated.RemoteParallelism, 1, 16, nameof(updated.RemoteParallelism));
        ModuleSettingsJson.RequireRange(updated.OnnxMaxSequenceLength, 1, 8_192, nameof(updated.OnnxMaxSequenceLength));
        ModuleSettingsJson.RequireRange(updated.OnnxMaxThreads, 1, 64, nameof(updated.OnnxMaxThreads));
        ModuleSettingsJson.RequireRange(updated.ChunkLines, 1, 10_000, nameof(updated.ChunkLines));
        ModuleSettingsJson.RequireRange(updated.ChunkOverlapLines, 0, updated.ChunkLines - 1, nameof(updated.ChunkOverlapLines));
        ModuleSettingsJson.RequireRange(updated.MaxFileSizeKb, 1, 102_400, nameof(updated.MaxFileSizeKb));
        ModuleSettingsJson.RequireRange(updated.DefaultTopK, 1, 1_000, nameof(updated.DefaultTopK));
        ModuleSettingsJson.RequireRange(updated.GitSyncIntervalMinutes, 1, 1_440, nameof(updated.GitSyncIntervalMinutes));

        CodeVectorSettings current = Settings;
        BackendType oldBackend = current.BackendType;
        string oldOnnxFolder = current.OnnxModelFolder;
        string oldDbPath = current.VectorDatabasePath;
        int oldSyncInterval = current.GitSyncIntervalMinutes;
        int oldParallelism = current.RemoteParallelism;

        CopySettings(updated, current);
        Repository.SaveSettings(current);

        // Same invalidation conditions as the WinForms config page. The dashboard endpoint has no
        // UI synchronization context, so blocking on these short teardown/rebuild tasks is safe.
        if (oldDbPath != current.VectorDatabasePath)
            InvalidateVectorDatabaseAsync().GetAwaiter().GetResult();
        if (oldBackend != current.BackendType || oldOnnxFolder != current.OnnxModelFolder)
            InvalidateEmbeddingBackendAsync().GetAwaiter().GetResult();
        if (oldSyncInterval != current.GitSyncIntervalMinutes)
            InvalidateMirrorManagerAsync().GetAwaiter().GetResult();
        if (oldParallelism != current.RemoteParallelism)
            RestartEngineAsync("parallelism changed").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Copies every value in place so engines holding the live settings instance observe the
    /// change (replacing the field would leave them on the old object).
    /// </summary>
    private static void CopySettings(CodeVectorSettings source, CodeVectorSettings target)
    {
        target.BackendType = source.BackendType;
        target.RemoteUrl = source.RemoteUrl;
        target.RemoteModel = source.RemoteModel;
        target.RemoteCredentialName = source.RemoteCredentialName;
        target.RemoteTimeoutSeconds = source.RemoteTimeoutSeconds;
        target.RemoteParallelism = source.RemoteParallelism;
        target.OnnxModelFolder = source.OnnxModelFolder;
        target.OnnxMaxSequenceLength = source.OnnxMaxSequenceLength;
        target.OnnxMaxThreads = source.OnnxMaxThreads;
        target.ChunkLines = source.ChunkLines;
        target.ChunkOverlapLines = source.ChunkOverlapLines;
        target.MaxFileSizeKb = source.MaxFileSizeKb;
        target.DefaultTopK = source.DefaultTopK;
        target.DefaultCollection = source.DefaultCollection;
        target.SearchEnabled = source.SearchEnabled;
        target.IndexEnabled = source.IndexEnabled;
        target.SyncRepoEnabled = source.SyncRepoEnabled;
        target.StatusEnabled = source.StatusEnabled;
        target.RemoveEnabled = source.RemoveEnabled;
        target.ReindexEnabled = source.ReindexEnabled;
        target.GitSyncIntervalMinutes = source.GitSyncIntervalMinutes;
        target.McpLogLevel = source.McpLogLevel;
        target.VectorDatabasePath = source.VectorDatabasePath;
    }
}