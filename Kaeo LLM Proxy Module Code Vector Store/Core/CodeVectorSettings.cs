

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorSettings
{
	public BackendType BackendType { get; set; } = BackendType.Remote;
	public string RemoteUrl { get; set; } = string.Empty;
	public string RemoteModel { get; set; } = string.Empty;
	public string RemoteCredentialName { get; set; } = string.Empty;
	public int RemoteTimeoutSeconds { get; set; } = 60;
	public string OnnxModelFolder { get; set; } = string.Empty;
	public int OnnxMaxSequenceLength { get; set; } = 512;
	public int OnnxMaxThreads { get; set; } = 4;
	public int ChunkLines { get; set; } = 60;
	public int ChunkOverlapLines { get; set; } = 10;
	public int MaxFileSizeKb { get; set; } = 256;
	public int DefaultTopK { get; set; } = 8;
	public string DefaultCollection { get; set; } = "default";
	public bool SearchEnabled { get; set; } = true;
	public bool IndexEnabled { get; set; } = true;
	public bool SyncRepoEnabled { get; set; } = true;
	public bool StatusEnabled { get; set; } = true;
	public bool RemoveEnabled { get; set; } = true;
	public bool ReindexEnabled { get; set; } = true;
	public int GitSyncIntervalMinutes { get; set; } = 15;
	public CodeVectorMcpLogLevel McpLogLevel { get; set; } = CodeVectorMcpLogLevel.Connectivity;

		/// <summary>
		/// Maximum number of concurrent embedding requests to the remote backend.
		/// Controls both parallel file workers and in-flight batch requests. Min: 1, Max: 16. Default: 4.
		/// </summary>
		public int RemoteParallelism { get; set; } = 4;

		/// <summary>
		/// Root directory where git mirrors are checked out. Defaults to <c>moduleDataDir/mirrors</c>.
		/// Set to a different path to monitor a specific repository externally or share mirrors across sessions.
		/// </summary>
		public string VectorDatabasePath { get; set; } = string.Empty;
}
