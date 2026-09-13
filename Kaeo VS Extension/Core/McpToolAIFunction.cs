using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Wraps one MCP tool definition (the Ollama/OpenAI shape returned by
/// <see cref="McpServerManager.GetAvailableToolDefinitions"/>: a "function" object with
/// name, description and parameters schema) as a Microsoft.Extensions.AI AIFunction, so
/// ChatOptions can project it into the request payload. Invocation routes to the owning
/// MCP server; the agent runtime's manual loop performs permission gating before calling
/// this type, so InvokeCoreAsync itself stays approval-free for middleware use later.
/// </summary>
internal sealed class McpToolAIFunction : AIFunction
{
    private readonly McpServerManager _mcp;
    private readonly JsonElement _schema;

    public McpToolAIFunction(JsonNode definition, McpServerManager mcp)
    {
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));

        if (definition is not JsonObject root || root["function"] is not JsonObject fn)
            throw new ArgumentException("Tool definition must be a JSON object with a 'function' member.", nameof(definition));

        Name = TryGetString(fn, "name") ?? string.Empty;
        Description = TryGetString(fn, "description") ?? string.Empty;

        JsonNode? parameters = fn["parameters"];
        _schema = parameters is not null
            ? JsonDocument.Parse(parameters.ToJsonString()).RootElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description { get; }

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>(arguments);
        string? argsJson = args.Count > 0 ? JsonSerializer.Serialize(args) : null;
        return await _mcp.ExecuteToolAsync(Name, argsJson, cancellationToken);
    }

    private static string? TryGetString(JsonObject obj, string property)
        => obj[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
