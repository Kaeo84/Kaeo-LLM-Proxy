using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal sealed class ExtensionSettingsStore
{
    private readonly string _path;

    public ExtensionSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaeoVsExtension", "settings.jsonc");
    }

    public async Task<ExtensionSettings> LoadAsync()
    {
        if (!File.Exists(_path))
            return new ExtensionSettings();

        // File.ReadAllTextAsync is net6+; run the synchronous version on a thread-pool
        // thread so the net48 target stays compatible (settings file is small).
        var text = await Task.Run(() => File.ReadAllText(_path)).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ExtensionSettings>(text) ?? new ExtensionSettings();
        }
        catch
        {
            return new ExtensionSettings();
        }
    }

    public async Task SaveAsync(ExtensionSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir!);

        var text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        // File.WriteAllTextAsync is net6+; run the synchronous version on a thread-pool
        // thread for net48 compatibility.
        await Task.Run(() => File.WriteAllText(_path, text)).ConfigureAwait(false);
    }
}

internal sealed class ExtensionSettings
{
    public Defaults? Defaults { get; set; } = new Defaults();
    public Connection[]? Connections { get; set; } = Array.Empty<Connection>();
    public Agent[]? Agents { get; set; } = Array.Empty<Agent>();
    public McpServer[]? McpServers { get; set; } = Array.Empty<McpServer>();
    public Logging? Logging { get; set; } = new Logging();
}

internal sealed class Defaults
{
    public string? Agent { get; set; }
    public string? Mode { get; set; }
    public string? Model { get; set; }
    public bool AutoAttachContext { get; set; } = true;
}

internal sealed class Connection
{
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
    public ModelEntry[]? Models { get; set; } = Array.Empty<ModelEntry>();
}

internal sealed class ModelEntry
{
    public string? Name { get; set; }
    public string[]? Capabilities { get; set; }
    public long ContextSize { get; set; }
    public bool Pinned { get; set; }
}

internal sealed class Agent
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public string[]? Tools { get; set; }
    public string? DefaultModel { get; set; }
}

internal sealed class McpServer
{
    public string? Name { get; set; }
    public string? Transport { get; set; }
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? Command { get; set; }
    public string[]? Args { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Stale { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public McpTool[]? Tools { get; set; } = Array.Empty<McpTool>();
}

internal sealed class McpTool
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonNode? Schema { get; set; }
    public bool Enabled { get; set; } = true;
}

internal sealed class Logging
{
    public string? Level { get; set; } = "Information";
    public string? File { get; set; }
}
