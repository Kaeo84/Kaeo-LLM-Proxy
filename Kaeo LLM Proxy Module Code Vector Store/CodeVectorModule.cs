using System.Reflection;
using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Module.CodeVector;

public sealed class CodeVectorModule : IKaeoModule, IMcpToolModule, IRunnableModule, IHelpModule
{
	public const string Version = "1.0.0";

	private ModuleContext? _context;
	private CodeVectorRepository? _repository;
	private CodeVectorDatabase? _vectorDb;
	private CodeVectorSettings _settings = new();
	private IEmbeddingBackend? _embeddingBackend;
	private IndexingEngine? _indexingEngine;
	private GitMirrorManager? _mirrorManager;
	private VectorSearchEngine? _searchEngine;
	private CodeVectorActivityLogger? _activity;
    private readonly object _vectorDatabaseLock = new();
    private string? _moduleDataDir;
    private string? _dataDirectory;
    private bool _started;

	public string Id => "kaeo.codevector";
	public string Name => "Code Vector Store";
	string IKaeoModule.Version => Version;
	public string Description => "Embeddings + vector store for code search via MCP tools. "
		+ "Supports remote HTTP and local ONNX CPU embedding backends, "
		+ "agent-push indexing, and server-side git mirrors with LibGit2Sharp.";

	internal CodeVectorRepository Repository =>
		_repository ?? throw new InvalidOperationException("Module not initialized.");
    internal CodeVectorDatabase VectorDb => EnsureVectorDatabase();
	internal IEmbeddingBackend EmbeddingBackend =>
		_embeddingBackend ?? throw new InvalidOperationException("Embedding backend not initialized.");
    internal IndexingEngine Indexer => EnsureIndexingEngine();
    internal GitMirrorManager MirrorManager => EnsureMirrorManager();
    internal VectorSearchEngine SearchEngine => EnsureSearchEngine();
	internal CodeVectorActivityLogger Activity =>
		_activity ?? throw new InvalidOperationException("Module not initialized.");
	internal ISecretProvider Secrets =>
		_context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");
	internal HostInfo Host =>
		_context?.Host ?? throw new InvalidOperationException("Module not initialized.");
	internal CodeVectorSettings Settings => _settings;

	/// <summary>
	/// Disposes the cached vector database and dependent engines so the next access
	/// re-resolves the path from the updated <see cref="Settings.VectorDatabasePath"/>.
	/// </summary>
	internal void InvalidateVectorDatabase()
	{
		lock (_vectorDatabaseLock)
		{
			_searchEngine = null;
			_indexingEngine = null;
			_vectorDb?.Dispose();
			_vectorDb = null;
		}
	}

	/// <summary>
	/// Disposes the cached embedding backend and dependent engines, then recreates the
	/// backend from the current settings. Call when the backend type or ONNX model folder changes.
	/// </summary>
	internal void InvalidateEmbeddingBackend()
	{
		lock (_vectorDatabaseLock)
		{
			_mirrorManager = null;
			_searchEngine = null;
			_indexingEngine = null;
			_embeddingBackend?.Dispose();
			_embeddingBackend = EmbeddingBackendFactory.Create(_settings, Secrets, Host);
		}
	}

	/// <summary>
	/// Stops and disposes the cached mirror manager so the next access recreates it
	/// with the updated sync interval.
	/// </summary>
	internal void InvalidateMirrorManager()
	{
		lock (_vectorDatabaseLock)
		{
			_mirrorManager?.StopTimer();
			_mirrorManager = null;
		}
	}

	public void Initialize(ModuleContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		_context = context;
		CodeVectorRepository.ApplySharedSchema(context.Database);
		_repository = new CodeVectorRepository(context.Database);
		_settings = _repository.LoadSettings();

		_dataDirectory = context.DataDirectory;
		_moduleDataDir = Path.Combine(context.DataDirectory, "codevector");
		_activity = new CodeVectorActivityLogger(context.ActivityLog, () => _settings.McpLogLevel);
		_embeddingBackend = EmbeddingBackendFactory.Create(_settings, context.Secrets, context.Host);

		// Make the embedded LibGit2Sharp native library available before any git operation
		// triggers its first native load (Repository.Clone/Fetch, etc.).
		EnsureLibGit2NativeLibraryAvailable();
	}

	/// <summary>
	/// Makes the embedded LibGit2Sharp native library (git2-*.dll) available to the native
	/// loader. The native DLL is embedded into this module's assembly at build time as a
	/// 'moduledep/' manifest resource (see ModuleBuild.targets). At init we extract it to a
	/// stable directory under the module data dir and point LibGit2Sharp at it via
	/// GlobalSettings.NativeLibraryPath, which its DllImport resolver consults on the first
	/// native load. This keeps the module self-contained: no separate native file needs to
	/// sit beside the module DLL.
	/// </summary>
	private void EnsureLibGit2NativeLibraryAvailable()
	{
		try
		{
			string? moduleDataDir = _moduleDataDir;
			if (moduleDataDir is null)
				return;

			Assembly moduleAssembly = Assembly.GetExecutingAssembly();
			string? resourceName = moduleAssembly.GetManifestResourceNames()
				.FirstOrDefault(n => n.StartsWith("moduledep/git2-", StringComparison.OrdinalIgnoreCase)
					&& n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

			if (resourceName is null)
			{
				// Not embedded (e.g. built before the embedding change); fall back to the
				// on-disk resolution handled by the module load context.
				Log.Debug("No embedded LibGit2Sharp native resource found; relying on on-disk resolution.");
				return;
			}

			string nativeFileName = Path.GetFileName(resourceName);
			string nativeDir = Path.Combine(moduleDataDir, "native");
			Directory.CreateDirectory(nativeDir);
			string nativePath = Path.Combine(nativeDir, nativeFileName);

			using (Stream? stream = moduleAssembly.GetManifestResourceStream(resourceName))
			{
				if (stream is null)
				{
					Log.Warning("Embedded resource {Resource} not found at runtime.", resourceName);
					return;
				}
				long size = stream.Length;
				if (!File.Exists(nativePath) || new FileInfo(nativePath).Length != size)
				{
					using var file = File.Create(nativePath);
					stream.CopyTo(file);
					Log.Information("Extracted LibGit2Sharp native library to {Path}", nativePath);
				}
			}

			// Point LibGit2Sharp at the extracted directory via its built-in
			// GlobalSettings.NativeLibraryPath (LibGit2Sharp appends the git2 file name
			// itself). Must be set before the first native call. Do NOT register our own
			// NativeLibrary.SetDllImportResolver here: LibGit2Sharp's NativeMethods type
			// initializer registers its own resolver, and a pre-existing registration makes
			// that initializer throw InvalidOperationException — the exact
			// "type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception"
			// failure this method is meant to prevent.
			try
			{
				LibGit2Sharp.GlobalSettings.NativeLibraryPath = nativeDir;
				Log.Debug("Set LibGit2Sharp GlobalSettings.NativeLibraryPath = {Dir}", nativeDir);
			}
			catch (LibGit2Sharp.LibGit2SharpException ex)
			{
				Log.Warning(ex, "Could not set LibGit2Sharp NativeLibraryPath; relying on default resolution.");
			}
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "Failed to prepare LibGit2Sharp native library; git mirrors may fail to load.");
		}
	}

	public System.Windows.Forms.TabPage CreateConfigPage() => new CodeVectorConfigPage(this);
	public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) => [new CodeVectorTools(this, session)];
	public bool IsRunning => _indexingEngine?.IsRunning == true;
	public event EventHandler<string>? StatusChanged;

	/// <summary>Why the indexing engine last stopped, or null while it is running.</summary>
	internal string? EngineStopReason => _indexingEngine?.StopReason;

	/// <summary>
	/// Starts (or restarts) only the indexing engine. Does not touch the mirror sync timer or
	/// dispose the embedding backend / vector database.
	/// </summary>
	internal void StartEngine()
	{
		Indexer.Start();
	}

	/// <summary>
	/// Stops only the indexing engine, recording why. Does not dispose the embedding backend or
	/// vector database, so search and re-indexing remain available after a stop.
	/// </summary>
	internal Task StopEngineAsync(string? reason = null)
	{
		if (_indexingEngine is not { IsRunning: true })
			return Task.CompletedTask;
		return _indexingEngine.StopAsync(reason);
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		_started = true;
		if (_indexingEngine is not null)
			_indexingEngine.Start();
		if (_mirrorManager is not null)
			_mirrorManager.StartTimer();
		StatusChanged?.Invoke(this, "Running");
		return Task.CompletedTask;
	}

	public async Task StopAsync()
	{
		_started = false;
		_mirrorManager?.StopTimer();
		if (_indexingEngine is { IsRunning: true })
			await _indexingEngine.StopAsync("module stopped");
		_embeddingBackend?.Dispose();
		_vectorDb?.Dispose();
		StatusChanged?.Invoke(this, "Stopped");
	}

    private CodeVectorDatabase EnsureVectorDatabase()
    {
        if (_vectorDb is not null)
            return _vectorDb;

        lock (_vectorDatabaseLock)
        {
            if (_vectorDb is not null)
                return _vectorDb;

            string moduleDataDir = _moduleDataDir ?? throw new InvalidOperationException("Module not initialized.");
            string vectorDbPath = string.IsNullOrWhiteSpace(_settings.VectorDatabasePath)
                ? Path.Combine(moduleDataDir, "codevectordb")
                : (Path.IsPathRooted(_settings.VectorDatabasePath)
                    ? _settings.VectorDatabasePath
                    : Path.Combine(moduleDataDir, _settings.VectorDatabasePath));
            string? vectorDbDirectory = Path.GetDirectoryName(vectorDbPath);
            if (!string.IsNullOrWhiteSpace(vectorDbDirectory))
                Directory.CreateDirectory(vectorDbDirectory);

            Log.Information("Vector database: {Path}", vectorDbPath);
            _vectorDb = new CodeVectorDatabase(vectorDbPath);
            return _vectorDb;
        }
    }

    private IndexingEngine EnsureIndexingEngine()
    {
        if (_indexingEngine is not null)
            return _indexingEngine;

        lock (_vectorDatabaseLock)
        {
            _indexingEngine ??= new IndexingEngine(EnsureVectorDatabase(), EmbeddingBackend, _settings, _activity);
            if (_started)
                _indexingEngine.Start();
            return _indexingEngine;
        }
    }

    private GitMirrorManager EnsureMirrorManager()
    {
        if (_mirrorManager is not null)
            return _mirrorManager;

        lock (_vectorDatabaseLock)
        {
            string moduleDataDir = _moduleDataDir ?? throw new InvalidOperationException("Module not initialized.");
            _mirrorManager ??= new GitMirrorManager(moduleDataDir, Repository, EnsureVectorDatabase(), EnsureIndexingEngine(), _settings, _activity, Secrets);
            if (_started)
                _mirrorManager.StartTimer();
            return _mirrorManager;
        }
    }

    private VectorSearchEngine EnsureSearchEngine()
    {
        if (_searchEngine is not null)
            return _searchEngine;

        lock (_vectorDatabaseLock)
        {
            _searchEngine ??= new VectorSearchEngine(EnsureVectorDatabase());
            return _searchEngine;
        }
    }

	public System.Windows.Forms.TabPage CreateHelpPage() => CodeVectorHelpPage.Create();

}
