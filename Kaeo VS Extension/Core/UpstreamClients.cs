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
/// Parsing/display helpers for the persisted reasoning-display preference
/// (<see cref="Defaults.ReasoningDisplay"/>).
/// </summary>
internal static class ReasoningDisplayKinds
{
    public const string Collapsed = "Collapsed";
    public const string Inline = "Inline";
    public const string Hidden = "Hidden";

    public static readonly IReadOnlyList<string> DisplayNames = new[] { Collapsed, Inline, Hidden };

    public static string Parse(string? value) => value switch
    {
        Inline => Inline,
        Hidden => Hidden,
        _ => Collapsed,
    };
}

/// <summary>
/// Parses the per-model "reasoningSource" string (see <see cref="ModelEntry.ReasoningSource"/>)
/// into the adapter's wire-mapping mode.
/// </summary>
internal static class ReasoningSources
{
    public static ReasoningSource Parse(string? value) => value switch
    {
        "ThinkingField" => ReasoningSource.ThinkingField,
        "InlineTags" => ReasoningSource.InlineTags,
        "Disabled" => ReasoningSource.Disabled,
        _ => ReasoningSource.Auto,
    };
}

/// <summary>
/// Contract every upstream backend implements for connection diagnostics and model
/// discovery. Chat itself flows through Microsoft.Extensions.AI (OllamaChatClient);
/// OpenAI/Anthropic implementations remain discovery stubs for the upcoming upstream
/// mappings (see the proxy Phase-B plan).
/// </summary>
internal interface IUpstreamClient
{
    Task<bool> HealthAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);
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
}
