using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Services.Mcp;
using Serilog;
using Kaeo.LlmProxy.Services.Translation;

namespace Kaeo.LlmProxy.Services;

partial class OllamaProxyHandler
{
    // â”€â”€ /api/tags â†’ configured proxy model names â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task HandleTagsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        // The Hidden flag is honored only by the two list endpoints (/v1/models and /api/tags)
        // so clients that auto-discover models do not pick up embeddings-only or otherwise
        // restricted mappings. Routing, heartbeats, keep-alive, and eligibility as a compaction
        // target are unaffected â€” a hidden mapping remains fully usable when named explicitly.
        var tags = new OllamaTagsResponse
        {
            Models = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !m.Hidden && !string.IsNullOrWhiteSpace(m.ProxyName))
                .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)
                .Select(CreateOllamaModelEntry)],
        };

        string tagsJson = JsonSerializer.Serialize(tags, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = tagsJson;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, tagsJson, ct);
    }

    // â”€â”€ /v1/models â†’ OpenAI-format model list with context_length â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task HandleV1ModelsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        // The Hidden flag is honored only by the two list endpoints (/v1/models and /api/tags)
        // so clients that auto-discover models do not pick up embeddings-only or otherwise
        // restricted mappings. Routing, heartbeats, keep-alive, and eligibility as a compaction
        // target are unaffected â€” a hidden mapping remains fully usable when named explicitly.
        var response = new LlamaCppModelsResponse
        {
            Data = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !m.Hidden && !string.IsNullOrWhiteSpace(m.ProxyName))
                .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    string name = string.IsNullOrWhiteSpace(m.ProxyName) ? m.ModelName : m.ProxyName;
                    return new LlamaCppModel
                    {
                        Id = name,
                        OwnedBy = "kaeo-proxy",
                        ContextLength = m.GetEffectiveContextWindow(),
                        Capabilities = BuildCapabilities(m),
                    };
                })],
        };

        string json = JsonSerializer.Serialize(response, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = json;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, json, ct);
    }

    // â”€â”€ GET /v1/models/{model} â†’ single-model lookup from local mappings â”€â”€â”€â”€â”€

    /// <summary>
    /// Answers <c>GET /v1/models/{model}</c> entirely from the local mapping table, mirroring
    /// <c>/api/show</c>. Upstreams vary wildly in whether/how they support a single-model lookup
    /// (some return 404, some 400, some nothing at all), and only the proxy knows its exposed
    /// names â€” building the response locally keeps model availability consistent with what
    /// <c>/v1/models</c> reports.
    /// </summary>
    private async Task HandleV1ModelAsync(string path, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string requestedModel = Uri.UnescapeDataString(path["/v1/models/".Length..]);
        ModelMapping? mapping = _settings.FindModelMapping(requestedModel);
        log.Model = requestedModel;

        if (mapping is null)
        {
            log.StatusCode = 404;
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Model '{requestedModel}' not found in configured mappings.";

            resp.StatusCode = 404;
            await WriteJsonAsync(resp, new
            {
                error = new
                {
                    message = $"The model '{requestedModel}' does not exist or is not enabled in the proxy configuration.",
                    type = "invalid_request_error",
                    param = "model",
                    code = "model_not_found",
                },
            }, ct);
            return;
        }

        string name = string.IsNullOrWhiteSpace(mapping.ProxyName) ? mapping.ModelName : mapping.ProxyName;
        var model = new LlamaCppModel
        {
            Id = name,
            OwnedBy = "kaeo-proxy",
            ContextLength = mapping.GetEffectiveContextWindow(),
            Capabilities = BuildCapabilities(mapping),
        };

        string modelJson = JsonSerializer.Serialize(model, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = modelJson;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, modelJson, ct);
    }

}
