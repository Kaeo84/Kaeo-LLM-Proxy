using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Microsoft.Extensions.AI chat client over the proxy's Ollama-compatible /api/chat
/// endpoint. Streams NDJSON and maps each line through <see cref="MeaiAdapter"/>, so
/// reasoning arrives as <see cref="TextReasoningContent"/> (structured "thinking" field,
/// or inline tag blocks for models the proxy leaves inline) and tool calls arrive as
/// <see cref="FunctionCallContent"/>.
/// </summary>
internal sealed class OllamaChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private readonly string? _apiKey;
    private readonly ReasoningSource _reasoningSource;

    public OllamaChatClient(
        string baseUrl,
        string defaultModel,
        string? apiKey = null,
        HttpClient? httpClient = null,
        ReasoningSource reasoningSource = ReasoningSource.Auto)
    {
        if (baseUrl is null)
            throw new ArgumentNullException(nameof(baseUrl));

        _baseUrl = baseUrl.TrimEnd('/');
        _defaultModel = defaultModel ?? string.Empty;
        _apiKey = apiKey;
        _reasoningSource = reasoningSource;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient();
            // net48 has no SocketsHttpHandler; recycle pooled connections via the service-point
            // lease so a long-lived client picks up DNS changes instead of pinning sockets.
            ServicePointManager.FindServicePoint(new Uri(_baseUrl)).ConnectionLeaseTimeout =
                (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            _ownsHttpClient = true;
        }

        // Auth is applied per request (see StreamAsync), never on DefaultRequestHeaders:
        // the client may be shared across connections with different keys, and mutating the
        // default headers is not thread-safe against in-flight sends (credential mix-up).
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            updates.Add(update);

        return MeaiAdapter.ToChatResponse(updates);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        JsonObject payload = BuildPayload(messages, options);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpointUri("/api/chat"));
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        HttpHelpers.StampClientIdentity(request);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Surface the proxy's error body instead of a bare status code: the /api/chat
        // path answers 4xx with {"error":"..."} (malformed body, unknown model, upstream
        // rejection), and without it the only diagnostic is the HTTP reason phrase.
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"{response.StatusCode} {(int)response.StatusCode} from {request.RequestUri}"
                + (string.IsNullOrWhiteSpace(errorBody) ? string.Empty : $": {Truncate(errorBody, 2000)}"));
        }

        // net48 has only the parameterless ReadAsStreamAsync and ReadLineAsync has no
        // CancellationToken overload, so a stalled upstream (no more NDJSON lines) would
        // otherwise ignore Stop indefinitely — HttpClient's timeout does not cover the
        // response body after headers. Registering cancellation to dispose the stream faults
        // the pending read; the loop below translates that into OperationCanceledException.
        var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try { ((Stream)state!).Dispose(); }
            catch { /* best-effort: fault the pending read */ }
        }, responseStream);

        // One splitter per stream: inline thinking tags can span chunk boundaries.
        ThinkTagStreamSplitter? splitter = _reasoningSource is ReasoningSource.Auto or ReasoningSource.InlineTags
            ? new ThinkTagStreamSplitter()
            : null;

        string? line;
        while (true)
        {
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (line is null)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            ChatResponseUpdate? update = MeaiAdapter.ToResponseUpdate(doc.RootElement, _reasoningSource, splitter);
            if (update is not null)
                yield return update;
        }

        // Drain anything the splitter buffered (e.g. an unterminated thinking block).
        if (splitter is not null)
        {
            (string reasoning, string text) = splitter.Flush();
            List<AIContent> tail = [];
            if (reasoning.Length > 0)
                tail.Add(new TextReasoningContent(reasoning));
            if (text.Length > 0)
                tail.Add(new TextContent(text));

            if (tail.Count > 0)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = tail };
        }
    }

    /// <summary>
    /// Builds the endpoint URI without discarding a path in the base URL (see
    /// <see cref="HttpHelpers.BuildEndpointUri"/>).
    /// </summary>
    private Uri BuildEndpointUri(string endpointPath)
        => HttpHelpers.BuildEndpointUri(_baseUrl, endpointPath);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType is null)
            throw new ArgumentNullException(nameof(serviceType));
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(ChatClientMetadata))
            return new ChatClientMetadata("kaeo-proxy", new Uri(_baseUrl), _defaultModel);
        if (serviceType == typeof(OllamaChatClient))
            return this;
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private JsonObject BuildPayload(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        string model = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModel : options!.ModelId!;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = MeaiAdapter.ToOllamaMessagesJson(messages),
            ["stream"] = true,
        };

        if (options?.Temperature is not null)
            payload["options"] = new JsonObject { ["temperature"] = options.Temperature.Value };

        JsonArray? tools = MeaiAdapter.ToOllamaToolsJson(options?.Tools);
        if (tools is not null)
            payload["tools"] = tools;

        // Ask the proxy to surface the model's thinking (its per-model thinking
        // configuration decides how it is produced and normalized).
        if (_reasoningSource != ReasoningSource.Disabled)
            payload["think"] = true;

        return payload;
    }
}
