using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>Upstream server flavors a connection can target.</summary>
internal enum UpstreamKind
{
    Ollama,
    OpenAi,
    Anthropic,
}

/// <summary>Parsing/display helpers for the persisted upstream name (<see cref="Connection.Upstream"/>).</summary>
internal static class UpstreamKinds
{
    public static readonly IReadOnlyList<string> DisplayNames = new[] { "Ollama", "OpenAI", "Anthropic" };

    public static UpstreamKind Parse(string? value) => value switch
    {
        "OpenAI" => UpstreamKind.OpenAi,
        "Anthropic" => UpstreamKind.Anthropic,
        _ => UpstreamKind.Ollama,
    };

    public static string Display(UpstreamKind kind) => kind switch
    {
        UpstreamKind.OpenAi => "OpenAI",
        UpstreamKind.Anthropic => "Anthropic",
        _ => "Ollama",
    };
}

/// <summary>
/// Contract every upstream backend implements: health, model discovery, and streaming chat.
/// The chat payload shape is upstream-specific; see AgentRuntime.BuildChatPayload for the
/// Ollama shape used today. OpenAI/Anthropic implementations are stubs for now.
/// </summary>
internal interface IUpstreamClient
{
    Task<bool> HealthAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);
    IAsyncEnumerable<ChatChunk> StreamChatAsync(object payload, CancellationToken ct = default);
}

/// <summary>Creates the client implementation for a connection's configured upstream.</summary>
internal static class UpstreamClientFactory
{
    public static IUpstreamClient Create(UpstreamKind kind, string baseUrl, string? apiKey) => kind switch
    {
        UpstreamKind.OpenAi => new OpenAiUpstreamClient(baseUrl, apiKey),
        UpstreamKind.Anthropic => new AnthropicUpstreamClient(baseUrl, apiKey),
        _ => new OllamaApiClient(baseUrl, apiKey),
    };
}

/// <summary>
/// Stub for OpenAI-compatible upstreams (GET /models, POST /v1/chat/completions with SSE).
/// Every data call fails with a clear message until the API mapping is implemented.
/// </summary>
internal sealed class OpenAiUpstreamClient : IUpstreamClient
{
    public OpenAiUpstreamClient(string baseUrl, string? apiKey = null)
    {
        // Kept for the upcoming OpenAI mapping; unused until then.
        _ = baseUrl;
        _ = apiKey;
    }

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("The OpenAI upstream is stubbed but not implemented yet.");

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(object payload, CancellationToken ct = default)
        => throw new NotSupportedException("The OpenAI upstream is stubbed but not implemented yet.");
}

/// <summary>
/// Stub for Anthropic upstreams (GET /v1/models, POST /v1/messages with SSE).
/// Every data call fails with a clear message until the API mapping is implemented.
/// </summary>
internal sealed class AnthropicUpstreamClient : IUpstreamClient
{
    public AnthropicUpstreamClient(string baseUrl, string? apiKey = null)
    {
        // Kept for the upcoming Anthropic mapping; unused until then.
        _ = baseUrl;
        _ = apiKey;
    }

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("The Anthropic upstream is stubbed but not implemented yet.");

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(object payload, CancellationToken ct = default)
        => throw new NotSupportedException("The Anthropic upstream is stubbed but not implemented yet.");
}
