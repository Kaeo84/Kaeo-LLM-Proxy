using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// The dashboard's Test Console: sends one prompt straight to a model's upstream and relays the
/// reply to the browser as SSE. It deliberately mirrors the Windows GUI's Test Console — the body
/// goes through the same <see cref="OllamaProxyHandler.NormalizeRequestBody"/> pipeline, so model
/// rewriting, instruction injection, and the per-model sampling priorities apply identically — but
/// it talks to the upstream directly instead of through the proxy, so it works while the proxy is
/// stopped and needs no API key.
/// </summary>
/// <remarks>
/// The caller only ever sees models their own user record grants them
/// (<see cref="ProxyAccessService.IsModelAllowedForUser(ProxyUser?, string?)"/>); Admin users see every
/// enabled mapping. The model name in the request body is resolved against that filtered list, so a
/// caller cannot reach an ungranted model by naming it.
/// </remarks>
internal static partial class DashboardEndpoints
{
    private static readonly HttpClient TestConsoleClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static void MapTestConsole(WebApplication app, AppSettings settings, ProxyHostController controller)
    {
        // The model list is filtered per caller, so a Test Console session only ever offers the
        // models that session is allowed to use.
        app.MapGet("/api/testchat/models", (HttpContext context) =>
        {
            try
            {
                ProxyUser? user = CallerUser(context, controller);
                List<object> models = [.. settings.ModelMappings
                    .Where(m => m.IsEnabled && !string.IsNullOrWhiteSpace(m.ProxyName))
                    .Where(m => ProxyAccessService.IsModelAllowedForUser(user, m.ProxyName))
                    .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => (object)new
                    {
                        name = m.ProxyName,
                        temperature = m.Temperature,
                        temperatureLocked = m.TemperaturePriority == SamplingPriority.Provider,
                        repeatPenalty = m.RepeatPenalty,
                        repeatPenaltyLocked = m.RepeatPenaltyPriority == SamplingPriority.Provider,
                    })];

                return Results.Json(new { models });
            }
            catch (Exception ex) when (ex is IOException or SqliteException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapPost("/api/testchat", async (TestChatWrite write, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(write.Model))
                return Results.BadRequest(new { error = "Select a model first." });
            if (string.IsNullOrWhiteSpace(write.Prompt))
                return Results.BadRequest(new { error = "Enter a prompt first." });

            ProxyUser? user = CallerUser(context, controller);
            string modelName = write.Model.Trim();
            ModelMapping? mapping = settings.FindModelMapping(modelName);
            if (mapping is null || !mapping.IsEnabled)
                return Results.BadRequest(new { error = $"Model '{modelName}' is not configured." });
            if (!ProxyAccessService.IsModelAllowedForUser(user, mapping.ProxyName))
            {
                return Results.Json(
                    new { error = $"Your account does not have access to '{mapping.ProxyName}'." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Build and normalize the request before switching to the SSE response so a bad body
            // still gets a normal JSON error the browser can show in the modal.
            string requestBody;
            RequestLog log;
            try
            {
                (requestBody, log) = BuildTestRequest(write, mapping, settings, user, context);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Test Console could not build the request for model {Model}", mapping.ProxyName);
                return Results.BadRequest(new { error = $"{ex.GetType().Name}: {ex.Message}" });
            }

            await RelayUpstreamStreamAsync(context, settings, controller, mapping, requestBody, log);
            return Results.Empty;
        });
    }

    private static ProxyUser? CallerUser(HttpContext context, ProxyHostController controller)
    {
        int? userId = DashboardAuth.ResolveUserId(context);
        if (userId is null)
            return null;
        try
        {
            return controller.Database.FindProxyUser(userId.Value);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Serilog.Log.Warning(ex, "Test Console could not load the caller's user record");
            return null;
        }
    }

    private static (string Body, RequestLog Log) BuildTestRequest(
        TestChatWrite write,
        ModelMapping mapping,
        AppSettings settings,
        ProxyUser? user,
        HttpContext context)
    {
        JsonArray messages = [];
        if (!string.IsNullOrWhiteSpace(write.System))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = write.System.Trim(),
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            // The caller already rejected an empty prompt; the null-forgiving keeps the analyzer
            // from re-litigating it inside this helper.
            ["content"] = write.Prompt!.Trim(),
        });

        JsonObject payload = new()
        {
            ["model"] = mapping.ProxyName,
            ["stream"] = true,
            ["messages"] = messages,
            // Ask OpenAI-compatible upstreams to include a usage block in the terminal stream chunk
            // so the Test Console can report prompt/completion/reasoning tokens.
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        // Respect the model's sampling priority: Provider means the upstream's own value wins, so
        // the field is omitted rather than overridden with the dashboard's number.
        if (mapping.TemperaturePriority != SamplingPriority.Provider && write.Temperature is { } temperature)
            payload["temperature"] = temperature;
        if (mapping.RepeatPenaltyPriority != SamplingPriority.Provider && write.RepeatPenalty is { } repeatPenalty)
            payload["repeat_penalty"] = repeatPenalty;

        string clientBody = payload.ToJsonString();

        RequestLog log = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Method = "POST",
            OllamaPath = "(web test console)",
            UpstreamPath = "/v1/chat/completions",
            Model = mapping.ProxyName,
            OriginalModel = mapping.ProxyName,
            Streaming = true,
            Status = RequestStatus.Success,
            UserId = user?.Id,
            ClientAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = "Kaeo dashboard test console",
            RequestBytes = Encoding.UTF8.GetByteCount(clientBody),
        };

        string requestBody = OllamaProxyHandler.NormalizeRequestBody(
            clientBody,
            settings,
            log,
            model => OllamaProxyHandler.ShouldApplyThinkingCompatibility(settings, model));

        if (settings.CollectRequestDetails)
        {
            log.RequestBody = OllamaProxyHandler.RedactRequestBodyForLog(settings, clientBody, mapping.ProxyName);
            log.UpstreamRequestBody = OllamaProxyHandler.RedactRequestBodyForLog(settings, requestBody, mapping.ProxyName);
        }

        return (requestBody, log);
    }

    /// <summary>
    /// Posts the normalized body to the upstream and relays it to the browser as SSE frames the
    /// dashboard understands: <c>delta</c> for answer text, <c>thinking</c> for reasoning text,
    /// <c>usage</c> for the token block, then exactly one of <c>done</c> or <c>error</c>.
    /// </summary>
    private static async Task RelayUpstreamStreamAsync(
        HttpContext context,
        AppSettings settings,
        ProxyHostController controller,
        ModelMapping mapping,
        string requestBody,
        RequestLog log)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        StringBuilder answer = new();
        StringBuilder thinking = new();
        int chunkCount = 0;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            if (string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
                throw new InvalidOperationException("No upstream URL is configured for this model.");

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            int timeoutSeconds = mapping.UpstreamTimeoutSeconds > 0 ? mapping.UpstreamTimeoutSeconds : 300;
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            Uri requestUri = UpstreamUriHelper.BuildRequestUri(mapping.UpstreamUrl, "v1/chat/completions");
            using HttpRequestMessage request = new(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("text/event-stream");
            string? apiKey = settings.ResolveApiKey(mapping);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using HttpResponseMessage response = await TestConsoleClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                log.Status = RequestStatus.Error;
                log.StatusCode = (int)response.StatusCode;
                log.ErrorMessage = $"Upstream {(int)response.StatusCode}: {Truncate(body, 4000)}";
                await WriteEventAsync(context, new { type = "error", message = $"Upstream {(int)response.StatusCode}: {Truncate(body, 4000)}" }, timeoutCts.Token);
                return;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            bool isSse = mediaType is not null && mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
            if (!isSse)
            {
                // Upstream ignored stream=true. Surface the assistant message (or the raw body) in
                // one delta so the console still shows something useful.
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                string text = ExtractNonStreamingText(body) ?? Truncate(body, 20000);
                answer.Append(text);
                await WriteEventAsync(context, new { type = "delta", text }, timeoutCts.Token);
            }
            else
            {
                using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                using StreamReader reader = new(stream);
                while (true)
                {
                    string? line = await reader.ReadLineAsync(timeoutCts.Token);
                    if (line is null)
                        break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    string payload = line[5..].Trim();
                    if (payload.Length == 0)
                        continue;
                    if (payload == "[DONE]")
                        break;

                    chunkCount++;
                    if (!TryReadChunk(payload, out string? content, out string? reasoning, out JsonNode? usage))
                        continue;

                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        thinking.Append(reasoning);
                        await WriteEventAsync(context, new { type = "thinking", text = reasoning }, timeoutCts.Token);
                    }

                    if (!string.IsNullOrEmpty(content))
                    {
                        answer.Append(content);
                        await WriteEventAsync(context, new { type = "delta", text = content }, timeoutCts.Token);
                    }

                    if (usage is not null)
                        await WriteEventAsync(context, new { type = "usage", usage }, timeoutCts.Token);
                }
            }

            await WriteEventAsync(
                context,
                new
                {
                    type = "done",
                    durationMs = stopwatch.Elapsed.TotalMilliseconds,
                    chunks = chunkCount,
                    hasThinking = thinking.Length > 0,
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            log.Status = RequestStatus.Cancelled;
            log.ErrorMessage = "Cancelled by the dashboard.";
        }
        catch (Exception ex)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            Serilog.Log.Warning(ex, "Test Console request for model {Model} failed", mapping.ProxyName);
            try
            {
                await WriteEventAsync(
                    context,
                    new { type = "error", message = $"{ex.GetType().Name}: {ex.Message}" },
                    CancellationToken.None);
            }
            catch (Exception writeEx) when (writeEx is IOException or ObjectDisposedException)
            {
                // The browser went away mid-error; nothing left to report to.
            }
        }
        finally
        {
            stopwatch.Stop();
            log.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            log.ResponseBytes = Encoding.UTF8.GetByteCount(answer.ToString());
            if (log.CompletionTokens == 0 && chunkCount > 0)
                log.CompletionTokens = chunkCount;
            if (settings.CollectResponseDetails)
                log.ResponseBody = answer.ToString();

            // Test Console traffic shows up in the Requests tab like any other proxy request.
            RequestLog stored = log;
            controller.Stats.AddLog(stored);
        }
    }

    private static async Task WriteEventAsync(HttpContext context, object payload, CancellationToken cancellationToken)
    {
        if (context.RequestAborted.IsCancellationRequested)
            return;

        string json = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Pulls the answer text, reasoning text, and usage block out of one upstream SSE chunk. Both
    /// the OpenAI <c>reasoning_content</c> spelling and Ollama's <c>thinking</c> field are read so a
    /// local and a hosted upstream both show their reasoning.
    /// </summary>
    private static bool TryReadChunk(string payload, out string? content, out string? reasoning, out JsonNode? usage)
    {
        content = null;
        reasoning = null;
        usage = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("usage", out JsonElement usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                usage = JsonNode.Parse(usageElement.GetRawText());

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                // Ollama's /api/chat shape, and OpenAI-style responses that carry a message instead
                // of a delta, both land here.
                if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
                {
                    content = ReadString(message, "content");
                    reasoning = ReadString(message, "reasoning_content") ?? ReadString(message, "thinking");
                    return content is not null || reasoning is not null || usage is not null;
                }

                return usage is not null;
            }

            JsonElement choice = choices[0];
            if (!choice.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object)
                return usage is not null;

            content = ReadString(delta, "content");
            reasoning = ReadString(delta, "reasoning_content")
                ?? ReadString(delta, "reasoning")
                ?? ReadString(delta, "thinking");
            return content is not null || reasoning is not null || usage is not null;
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ExtractNonStreamingText(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out JsonElement message))
            {
                return ReadString(message, "content");
            }
        }
        catch (JsonException)
        {
            // Not JSON; the caller shows the raw body instead.
        }

        return null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}