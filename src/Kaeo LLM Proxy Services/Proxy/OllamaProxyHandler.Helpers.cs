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
    /// <summary>
    /// Converts Ollama's <c>format</c> field to an OpenAI <c>response_format</c> object.
    /// Ollama accepts:
    ///   â€¢ the literal string "json"  â†’ OpenAI {"type":"json_object"}
    ///   â€¢ a full JSON Schema object  â†’ OpenAI {"type":"json_schema","json_schema":{...}}
    ///   â€¢ an OpenAI-style object     â†’ forwarded as-is
    /// </summary>
    private static LlamaCppResponseFormat? ResolveResponseFormat(object? format)
    {
        if (format is null) return null;
        if (format is not JsonElement je) return null;

        if (je.ValueKind == JsonValueKind.String)
        {
            string? s = je.GetString();
            return string.Equals(s, "json", StringComparison.OrdinalIgnoreCase)
                ? new LlamaCppResponseFormat { Type = "json_object" }
                : null;
        }
        if (je.ValueKind != JsonValueKind.Object)
            return null;

        // OpenAI-style passthrough.
        if (je.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
        {
            string type = t.GetString() ?? "text";
            object? schema = je.TryGetProperty("json_schema", out JsonElement js)
                ? JsonSerializer.Deserialize<object>(js.GetRawText(), _jsonOptions)
                : null;
            return new LlamaCppResponseFormat { Type = type, JsonSchema = schema };
        }

        // Ollama JSON-Schema (object with no "type"): wrap as OpenAI json_schema.
        object? raw = JsonSerializer.Deserialize<object>(je.GetRawText(), _jsonOptions);
        return new LlamaCppResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new Dictionary<string, object?>
            {
                ["name"] = "ollama_format",
                ["strict"] = true,
                ["schema"] = raw,
            },
        };
    }

    /// <summary>
    /// Returns a short human-readable description of an Ollama <c>format</c> value for the
    /// debug audit trail. Accepts the Ollama <c>"json"</c> string, an OpenAI-style object with
    /// a <c>type</c>, or a bare JSON schema object.
    /// </summary>
    private static string FormatDescriptor(object? format)
    {
        if (format is not JsonElement je)
            return format is null ? "none" : format.GetType().Name;

        if (je.ValueKind == JsonValueKind.String)
            return $"\"{je.GetString()}\"";

        if (je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "object";
            return "json_schema (bare)";
        }

        return je.ValueKind.ToString();
    }

    private static LlamaCppMessage MapMessage(OllamaMessage m) =>
        new(m.Role, m.Content)
        {
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls is not null
                ? [.. m.ToolCalls.Select(tc => new LlamaCppToolCall
                    {
                        Id = string.IsNullOrWhiteSpace(tc.Id) ? Guid.NewGuid().ToString("N")[..8] : tc.Id!,
                        Function = tc.Function is null ? null : new LlamaCppToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments switch
                            {
                                null => null,
                                string s => s,
                                _ => JsonSerializer.Serialize(tc.Function.Arguments, _jsonOptions),
                            },
                        },
                    })]
                : null,
        };

    /// <summary>
    /// Maps Ollama messages to OpenAI/llama.cpp messages and rewrites tool_call IDs so that:
    ///   â€¢ each assistant tool_call gets a stable id (preserved if supplied, generated otherwise),
    ///   â€¢ each following role:"tool" reply that lacks an id is correlated to the most recent
    ///     unfulfilled assistant tool_call (by order, or by function name when available).
    /// OpenAI-compatible upstreams reject tool replies whose tool_call_id doesn't match.
    /// </summary>
    internal static List<LlamaCppMessage> MapMessagesWithToolCorrelation(List<OllamaMessage> source)
    {
        List<LlamaCppMessage> mapped = [.. source.Select(MapMessage)];
        // Queue of (id, function name) per pending assistant tool_call.
        Queue<(string Id, string? Name)> pending = new();

        for (int i = 0; i < mapped.Count; i++)
        {
            LlamaCppMessage msg = mapped[i];

            if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && msg.ToolCalls is { Count: > 0 })
            {
                foreach (LlamaCppToolCall tc in msg.ToolCalls)
                    pending.Enqueue((tc.Id, tc.Function?.Name));
            }
            else if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(msg.ToolCallId)
                && pending.Count > 0)
            {
                msg.ToolCallId = pending.Dequeue().Id;
            }
        }

        return mapped;
    }

    private static List<LlamaCppTool>? MapTools(List<OllamaTool>? tools) =>
        tools is null ? null
        : [.. tools.Select(t => new LlamaCppTool
            {
                Type = t.Type,
                Function = t.Function is null ? null : new LlamaCppToolFunction
                {
                    Name = t.Function.Name,
                    Description = t.Function.Description,
                    Parameters = t.Function.Parameters,
                },
            })];

    private static string CaptureXmlToolCallToken(string token, StringBuilder toolCallBuilder, ref bool isCapturing)
    {
        if (string.IsNullOrEmpty(token))
            return token;

        if (isCapturing)
        {
            toolCallBuilder.Append(token);
            if (toolCallBuilder.ToString().Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
                isCapturing = false;

            return string.Empty;
        }

        int startIndex = token.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return token;

        string visibleContent = token[..startIndex];
        toolCallBuilder.Append(token[startIndex..]);
        if (!toolCallBuilder.ToString().Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
            isCapturing = true;

        return visibleContent;
    }

    private static ToolCallExtraction ExtractXmlToolCalls(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new(content, null);

        MatchCollection matches = XmlToolCallRegex().Matches(content);

                if (matches.Count == 0)
                    return new(content, null);

                List<OllamaToolCall> toolCalls = [];
                foreach (Match match in matches)
                {
                    string name = match.Groups["name"].Value.Trim();
                    string body = match.Groups["body"].Value;
                    Dictionary<string, object?> arguments = new(StringComparer.OrdinalIgnoreCase);

                    foreach (Match parameterMatch in XmlToolCallParameterRegex().Matches(body))
                                {
                                    string parameterName = parameterMatch.Groups["name"].Value.Trim();
                                    string parameterValue = parameterMatch.Groups["value"].Value.Trim();
                                    arguments[parameterName] = ParseXmlToolParameterValue(parameterValue);
                                }

            toolCalls.Add(new OllamaToolCall
            {
                Function = new OllamaToolCallFunction
                {
                    Name = name,
                    Arguments = arguments,
                },
            });
        }

        string strippedContent = matches.Aggregate(content, static (current, match) => current.Replace(match.Value, string.Empty)).Trim();
        return new(strippedContent, toolCalls);
    }

    private static object? ParseXmlToolParameterValue(string value)
    {
        if (bool.TryParse(value, out bool booleanValue))
            return booleanValue;

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int integerValue))
            return integerValue;

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
            return doubleValue;

        return value;
    }

    private static List<OllamaToolCall>? MapToolCallsToOllama(List<LlamaCppToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return null;
        return [.. toolCalls.Select(tc =>
        {
            object? args = null;
            if (tc.Function?.Arguments is not null)
            {
                try { args = JsonSerializer.Deserialize<object>(tc.Function.Arguments, _jsonOptions); }
                catch { args = tc.Function.Arguments; }
            }
            return new OllamaToolCall
            {
                Id = string.IsNullOrWhiteSpace(tc.Id) ? null : tc.Id,
                Function = new OllamaToolCallFunction { Name = tc.Function?.Name ?? string.Empty, Arguments = args },
            };
        })];
    }

    private static void AppendStreamingToolCalls(
        Dictionary<int, StreamingToolCallBuilder> builders,
        List<LlamaCppToolCall>? toolCalls)
    {
        if (toolCalls is null)
            return;

        for (int i = 0; i < toolCalls.Count; i++)
        {
            LlamaCppToolCall toolCall = toolCalls[i];
            int index = toolCall.Index ?? i;
            if (!builders.TryGetValue(index, out StreamingToolCallBuilder? builder))
            {
                builder = new StreamingToolCallBuilder();
                builders[index] = builder;
            }

            if (!string.IsNullOrWhiteSpace(toolCall.Id))
                builder.Id = toolCall.Id;

            if (!string.IsNullOrWhiteSpace(toolCall.Function?.Name))
                builder.Name = toolCall.Function.Name;

            if (toolCall.Function?.Arguments is not null)
                builder.Arguments.Append(toolCall.Function.Arguments);
        }
    }

    private static List<OllamaToolCall>? BuildOllamaToolCalls(Dictionary<int, StreamingToolCallBuilder> builders)
    {
        if (builders.Count == 0)
            return null;

        return [.. builders
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                string arguments = pair.Value.Arguments.ToString();
                object? parsedArguments = null;
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    try { parsedArguments = JsonSerializer.Deserialize<object>(arguments, _jsonOptions); }
                    catch { parsedArguments = arguments; }
                }

                return new OllamaToolCall
                {
                    Id = string.IsNullOrWhiteSpace(pair.Value.Id) ? null : pair.Value.Id,
                    Function = new OllamaToolCallFunction
                    {
                        Name = pair.Value.Name,
                        Arguments = parsedArguments,
                    },
                };
            })];
    }

    private sealed class StreamingToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }

    private sealed record ToolCallExtraction(string? Content, List<OllamaToolCall>? ToolCalls);

    private static object ResolveEmbeddingInput(OllamaEmbeddingsRequest req)
    {
        if (req.Input is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            if (je.ValueKind == JsonValueKind.String)
                return je.GetString() ?? string.Empty;
        }
        if (req.Input is string s && !string.IsNullOrEmpty(s))
            return s;
        return req.Prompt ?? string.Empty;
    }

    // â”€â”€ Utility â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Reads the request body as a string, enforcing the configured
    /// <see cref="AppSettings.MaxRequestBodyBytes"/> limit. Rejecting oversized bodies before they
    /// are fully buffered protects the proxy from memory-exhaustion (DoS) attacks.
    /// </summary>
    /// <exception cref="RequestBodyTooLargeException">Thrown when the body exceeds the limit.</exception>
    /// <summary>
        /// Captures the request payload and proxy error body for a request the proxy rejected without
        /// handing it to a model. Called only on the reject branches, because reading the request body
        /// drains the stream â€” on any path that later forwards the body this would consume it first.
        /// </summary>
        /// <remarks>
        /// Best-effort: a request rejected before routing may have no readable body (a GET, a
        /// content-length-less request, an already-consumed stream), and failing to capture it must not
        /// change the response the client receives.
        /// </remarks>
        private async Task CaptureRejectedBodyAsync(HttpListenerRequest req, RequestLog log, string errorJson, CancellationToken ct)
        {
            if (_settings.CollectRequestDetails)
            {
                try
                {
                    // ReadBodyAsync enforces the size limit and throws for an oversized body; the catch
                    // below records the reason instead of losing the row.
                    string body = await ReadBodyAsync(req, ct);
                                    if (!string.IsNullOrWhiteSpace(body))
                                                        log.RequestBody = RedactUnmappedRequestBodyForLog(body);
                }
                catch (RequestBodyTooLargeException)
                {
                    // The bytes are deliberately not buffered, so there is nothing to capture.
                    log.RequestBody = null;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not capture the request body of a rejected request");
                }
            }

            if (_settings.CollectResponseDetails)
                log.ResponseBody = errorJson;
        }

        private async Task<string> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        long limit = _settings.MaxRequestBodyBytes;

        // Fast path: a declared Content-Length over the limit is rejected without reading the body.
        if (req.ContentLength64 > limit)
            throw new RequestBodyTooLargeException(limit, req.ContentLength64);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await req.InputStream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
                throw new RequestBodyTooLargeException(limit, total);

            buffer.Write(chunk, 0, read);
        }

        return req.ContentEncoding.GetString(buffer.ToArray());
    }

    /// <summary>Indicates a request body exceeded <see cref="AppSettings.MaxRequestBodyBytes"/>.</summary>
    private sealed class RequestBodyTooLargeException(long limit, long actual)
        : Exception($"Request body of {actual} bytes exceeds the configured maximum of {limit} bytes.")
    {
        public long Limit { get; } = limit;
        public long Actual { get; } = actual;
    }

    /// <summary>
    /// Deserializes a JSON request body into <typeparamref name="T"/>. When the body is missing,
    /// malformed, or does not match the expected shape, writes a 400 Bad Request response, marks
    /// the log as an error, and returns null so the caller can simply return.
    /// </summary>
    private static async Task<T?> TryDeserializeRequestAsync<T>(string body, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
        where T : class
    {
        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(body, _jsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Rejected malformed JSON request body");
            result = null;
        }

        if (result is null)
        {
            log.ErrorMessage = "Invalid or malformed request body.";
            RecordResponseStatus(resp, log, 400);
            await WriteJsonAsync(resp, new { error = "Invalid or malformed request body." }, ct);
        }

        return result;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object value, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(value, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    /// <summary>
    /// Best-effort delivery of an error response from a catch block. Returns false when the error could
    /// not be delivered, which happens when the response already started â€” <see cref="HttpListenerResponse"/>
    /// exposes no "headers sent" flag, so the only way to learn this is that setting the status code or
    /// content length throws. In that case the client is left holding a truncated response, and callers
    /// must say so in the log rather than swallowing the failure silently.
    /// </summary>
    private static async Task<bool> TryWriteErrorResponseAsync(
        HttpListenerResponse resp, RequestLog log, int statusCode, object payload, CancellationToken ct)
    {
        try
        {
            RecordResponseStatus(resp, log, statusCode);
            await WriteJsonAsync(resp, payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Net.HttpListenerException
            or IOException
            or ObjectDisposedException
            or OperationCanceledException)
        {
            Log.Debug(ex, "Could not deliver a {StatusCode} error response; the response had already started or the client disconnected", statusCode);
            return false;
        }
    }

    /// <summary>
    /// Closes a response that could not be answered properly, without throwing. Called after a failed
    /// error delivery so the connection is released rather than left dangling until it times out.
    /// </summary>
    private static void CloseResponseQuietly(HttpListenerResponse resp)
    {
        try { resp.Close(); }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Net.HttpListenerException
            or IOException
            or ObjectDisposedException)
        {
            Log.Debug(ex, "Response stream was already closed");
        }
    }

    /// <summary>
    /// Records that the client received a truncated response because no error could be delivered after
    /// the response had already started. Appended to <see cref="RequestLog.ErrorMessage"/> rather than
    /// stored in its own column: it qualifies the existing message and needs no schema change.
    /// </summary>
    private static void RecordUndeliverableError(RequestLog log, int statusCode)
    {
        const string suffix = " The response had already started, so no error could be delivered and the client received a truncated response.";

        // The response object is unusable here â€” it already committed â€” so the intended code is
        // recorded directly. Without this the entry would keep whatever status the streaming path
        // set before the failure, hiding that an error was never delivered.
        log.StatusCode = statusCode;
        log.Status = RequestLog.DeriveStatus(statusCode);

        log.ErrorMessage = string.IsNullOrEmpty(log.ErrorMessage)
            ? $"Failed to deliver a {statusCode} error response.{suffix}"
            : log.ErrorMessage + suffix;
    }

    /// <summary>
    /// Records an HTTP status on the log entry and sets it on the response together, so no branch
    /// can answer the client and then leave the entry at its <see cref="RequestStatus.Success"/>
    /// default. Every proxy-originated error goes through here.
    /// </summary>
    /// <remarks>
    /// The log entry is written first: <c>HttpListenerResponse.StatusCode</c> throws once the
    /// response has committed its headers, and recording first means the entry still carries the
    /// code the proxy intended to send when that happens.
    /// </remarks>
    private static void RecordResponseStatus(HttpListenerResponse resp, RequestLog log, int statusCode)
    {
        log.StatusCode = statusCode;
        log.Status = RequestLog.DeriveStatus(statusCode);
        resp.StatusCode = statusCode;
    }

    private static async Task WriteJsonRawAsync(HttpListenerResponse resp, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, string html, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    // â”€â”€ API Explorer (Scalar) / OpenAPI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Short-lived client used only to fetch OpenAPI documents reported by loaded modules when
    // rendering the explorer page. Only module-reported URLs are ever fetched (never user input).
    private static readonly HttpClient _explorerSpecClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
        })
        { Timeout = TimeSpan.FromSeconds(5) };

    private const string ApiExplorerHtmlTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Kaeo LLM Proxy â€” API Explorer</title>
            <style>
                body { margin: 0; padding: 0; }
                #kaeo-doc-selector {
                    position: fixed;
                    top: 10px;
                    right: 14px;
                    z-index: 10000;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                }
                #kaeo-doc-select { padding: 4px 8px; }
            </style>
        </head>
        <body>
            <div id="kaeo-doc-selector" hidden>
                <select id="kaeo-doc-select" aria-label="API document"></select>
            </div>
            <div id="kaeo-api-reference"></div>
            <script>
                var kaeoDocuments = /*DOCUMENTS*/[];
            </script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference@1"></script>
            <script>
                (function () {
                    var mount = document.getElementById('kaeo-api-reference');
                    var select = document.getElementById('kaeo-doc-select');
                    var selector = document.getElementById('kaeo-doc-selector');
                    var instance = null;

                    function configurationFor(doc) {
                        return { spec: { content: doc.spec } };
                    }

                    function loadDocument(index) {
                        var config = configurationFor(kaeoDocuments[index]);
                        if (instance && typeof instance.updateConfig === 'function') {
                            instance.updateConfig(config);
                            return;
                        }
                        mount.innerHTML = '';
                        instance = Scalar.createApiReference(mount, config);
                    }

                    kaeoDocuments.forEach(function (doc, i) {
                        var option = document.createElement('option');
                        option.value = String(i);
                        option.textContent = doc.label;
                        select.appendChild(option);
                    });

                    if (kaeoDocuments.length > 1) {
                        selector.hidden = false;
                        select.addEventListener('change', function () {
                            loadDocument(Number(select.value));
                        });
                    }

                    if (kaeoDocuments.length > 0) loadDocument(0);
                })();
            </script>
        </body>
        </html>
        """;

    /// <summary>
    /// Builds the Scalar explorer page at render time. The proxy's own OpenAPI document is
    /// embedded inline; documents reported by loaded modules (<see cref="IApiExplorerDocumentsProvider"/>)
    /// are fetched server-side and embedded too, so the browser never needs cross-origin access.
    /// Unreachable module documents are omitted gracefully.
    /// </summary>
    private async Task<string> BuildApiExplorerHtmlAsync(CancellationToken ct)
    {
        List<(string Label, string SpecJson)> documents = [("Kaeo LLM Proxy", OpenApiSpec)];

        // The built-in MCP server's document is embedded directly (same process, no fetch needed).
        if (_mcpServer.IsRunning && _mcpServer.ApiExplorer is { } mcpExplorer)
            documents.Add(("Kaeo LLM Proxy MCP", mcpExplorer.BuildSpecJson()));

        foreach (LoadedModule loaded in _moduleHost.LoadedModules)
        {
            if (loaded.Module is not IApiExplorerDocumentsProvider provider)
                continue;

            IReadOnlyList<ExplorerDocument> moduleDocuments;
            try
            {
                moduleDocuments = provider.GetExplorerDocuments();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Module {Name} failed to report explorer documents", loaded.Entry.Name);
                continue;
            }

            foreach (ExplorerDocument document in moduleDocuments)
            {
                string? specJson = await TryFetchExplorerSpecAsync(document.SpecUrl, ct).ConfigureAwait(false);
                if (specJson is null)
                {
                    Log.Information(
                        "API explorer: skipping '{Label}' ({Url}); the document is not reachable",
                        document.Label, document.SpecUrl);
                    continue;
                }

                documents.Add((document.Label, specJson));
            }
        }

        var documentsJson = new StringBuilder("[");
        for (int i = 0; i < documents.Count; i++)
        {
            if (i > 0)
                documentsJson.Append(',');

            // Spec content is embedded as validated raw JSON; "</" is escaped so an embedded
            // string value can never terminate the enclosing <script> block early.
            documentsJson
                .Append("{\"label\":")
                .Append(JsonSerializer.Serialize(documents[i].Label))
                .Append(",\"spec\":")
                .Append(documents[i].SpecJson.Replace("</", "<\\/"))
                .Append('}');
        }
        documentsJson.Append(']');

        return ApiExplorerHtmlTemplate.Replace("/*DOCUMENTS*/[]", documentsJson.ToString());
    }

    /// <summary>
    /// Fetches a module-reported OpenAPI document and validates that it is JSON. Returns null
    /// when the document is unreachable, empty, or not valid JSON.
    /// </summary>
    private static async Task<string?> TryFetchExplorerSpecAsync(string specUrl, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _explorerSpecClient.GetAsync(specUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            // Validate and normalize to compact JSON before embedding into the explorer page.
            using JsonDocument parsed = JsonDocument.Parse(content);
            return parsed.RootElement.GetRawText();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    private const string OpenApiSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Kaeo LLM Proxy",
            "description": "Ollama-compatible proxy that translates requests to OpenAI-compatible upstreams (llama.cpp, Qwen Cloud, etc.).",
            "version": "0.1.0"
          },
          "servers": [
            { "url": "/", "description": "This proxy" }
          ],
          "tags": [
            { "name": "Ollama Discovery", "description": "Ollama-compatible endpoints for model and version discovery. Answered locally from the mapping table â€” no upstream call." },
            { "name": "Ollama Generation", "description": "Ollama-compatible generation endpoints. The proxy translates these to OpenAI-compatible upstream calls." },
            { "name": "OpenAI Passthrough", "description": "Transparent passthrough to the upstream OpenAI-compatible /v1/* surface. No translation is performed." },
            { "name": "OpenAI Discovery", "description": "OpenAI-compatible endpoints for model discovery. Answered locally from the mapping table â€” no upstream call." }
          ],
          "paths": {
            "/api/version": {
              "get": {
                "summary": "Proxy version probe",
                "operationId": "getVersion",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Version information",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": { "version": { "type": "string" } }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/tags": {
              "get": {
                "summary": "List available models",
                "description": "Returns all enabled model mappings with their capabilities (text, chat, reasoning, vision, audio, function_calling, embeddings, code, image_generation).",
                "operationId": "listModels",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Model list",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "models": {
                              "type": "array",
                              "items": { "$ref": "#/components/schemas/ModelEntry" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/ps": {
              "get": {
                "summary": "List running models",
                "operationId": "listRunningModels",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Running model list",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "models": { "type": "array", "items": { "type": "object" } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/show": {
              "post": {
                "summary": "Show model information",
                "description": "Returns detailed information about a model including its capabilities and configuration.",
                "operationId": "showModel",
                "tags": ["Ollama Discovery"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model"],
                        "properties": {
                          "model": { "type": "string", "description": "Model name (e.g. 'myqwen' or 'myqwen:latest')" },
                          "name": { "type": "string", "description": "Alias for model" }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Model details",
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/ShowResponse" }
                      }
                    }
                  }
                }
              }
            },
            "/api/chat": {
              "post": {
                "summary": "Chat completion",
                "description": "Sends a chat conversation to the upstream model. Supports streaming (NDJSON) and non-streaming responses, tool calls, and vision (image) inputs.",
                "operationId": "chat",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/ChatRequest" }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Chat response (streaming NDJSON or single JSON)" }
                }
              }
            },
            "/api/generate": {
              "post": {
                "summary": "Text generation",
                "description": "Sends a prompt to the upstream model for text completion. Supports streaming and non-streaming.",
                "operationId": "generate",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/GenerateRequest" }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Generation response" }
                }
              }
            },
            "/api/embeddings": {
              "post": {
                "summary": "Generate embeddings",
                "operationId": "embeddings",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "prompt"],
                        "properties": {
                          "model": { "type": "string" },
                          "prompt": { "type": "string" }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Embedding vector" }
                }
              }
            },
            "/api/embed": {
              "post": {
                "summary": "Generate embeddings (alias)",
                "operationId": "embed",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "input"],
                        "properties": {
                          "model": { "type": "string" },
                          "input": {
                            "oneOf": [
                              { "type": "string" },
                              { "type": "array", "items": { "type": "string" } }
                            ]
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Embedding vectors" }
                }
              }
            },
            "/v1/chat/completions": {
              "post": {
                "summary": "OpenAI-compatible chat completions (passthrough)",
                "description": "Transparent passthrough to the upstream OpenAI-compatible /v1/chat/completions endpoint.",
                "operationId": "openAiChat",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI chat completion response" } }
              }
            },
            "/v1/completions": {
              "post": {
                "summary": "OpenAI-compatible completions (passthrough)",
                "operationId": "openAiCompletions",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI completion response" } }
              }
            },
            "/v1/models": {
              "get": {
                "summary": "OpenAI-compatible model list",
                "description": "Returns all enabled model mappings in OpenAI format, including the effective context_length (tokens) for each model.",
                "operationId": "openAiModels",
                "tags": ["OpenAI Discovery"],
                "responses": {
                  "200": {
                    "description": "OpenAI model list with context_length per model",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "object": { "type": "string" },
                            "data": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "id": { "type": "string" },
                                  "object": { "type": "string" },
                                  "created": { "type": "integer", "format": "int64" },
                                  "owned_by": { "type": "string" },
                                  "context_length": { "type": "integer", "description": "Effective context window in tokens." }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/v1/models/{model}": {
              "get": {
                "summary": "OpenAI-compatible single model lookup",
                "description": "Returns a single configured model mapping in OpenAI format. Answered locally from the mapping table (no upstream call), mirroring /api/show.",
                "operationId": "openAiModel",
                "tags": ["OpenAI Discovery"],
                "parameters": [
                  {
                    "name": "model",
                    "in": "path",
                    "required": true,
                    "schema": { "type": "string" },
                    "description": "Exposed proxy name (or upstream model name) of the mapping to look up."
                  }
                ],
                "responses": {
                  "200": {
                    "description": "OpenAI model object",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "id": { "type": "string" },
                            "object": { "type": "string" },
                            "created": { "type": "integer", "format": "int64" },
                            "owned_by": { "type": "string" },
                            "context_length": { "type": "integer", "description": "Effective context window in tokens." }
                          }
                        }
                      }
                    }
                  },
                  "404": { "description": "Model not found in configured mappings" }
                }
              }
            },
            "/v1/embeddings": {
              "post": {
                "summary": "OpenAI-compatible embeddings (passthrough)",
                "operationId": "openAiEmbeddings",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI embedding response" } }
              }
            },
            "/v1/responses/compact": {
              "post": {
                "summary": "Compact conversation context",
                "description": "Forwards the conversation to a model so that model produces the summary, and returns its response unchanged. Uses the mapping's compaction model when 'Redirect manual compaction' is enabled and a compaction model is selected; otherwise uses the model named in the request.",
                "operationId": "compactConversation",
                "tags": ["OpenAI Passthrough"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "input"],
                        "properties": {
                          "model": { "type": "string", "description": "Model name to use for compaction" },
                          "input": {
                            "type": "array",
                            "description": "Conversation messages to compact",
                            "items": {
                              "type": "object",
                              "properties": {
                                "role": { "type": "string" },
                                "content": { "type": "string" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Compacted conversation response" },
                  "502": { "description": "Upstream server error during compaction" }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "ModelEntry": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "model": { "type": "string" },
                  "modified_at": { "type": "string", "format": "date-time" },
                  "size": { "type": "integer", "format": "int64" },
                  "digest": { "type": "string" },
                  "details": {
                    "type": "object",
                    "properties": {
                      "parent_model": { "type": "string" },
                      "format": { "type": "string" },
                      "family": { "type": "string" },
                      "families": { "type": "array", "items": { "type": "string" } },
                      "parameter_size": { "type": "string" },
                      "quantization_level": { "type": "string" }
                    }
                  },
                  "capabilities": {
                    "type": "array",
                    "items": { "type": "string", "enum": ["text", "chat", "reasoning", "vision", "audio", "function_calling", "embeddings", "code", "image_generation"] },
                    "description": "Advertised model capabilities. Contains exactly the capabilities enabled per-mapping."
                  },
                  "context_length": { "type": "integer", "description": "Effective context window in tokens, used by clients for compaction thresholds." }
                }
              },
              "ShowResponse": {
                "type": "object",
                "properties": {
                  "modelfile": { "type": "string" },
                  "parameters": { "type": "string" },
                  "template": { "type": "string" },
                  "details": { "$ref": "#/components/schemas/ModelEntry/properties/details" },
                  "model_info": { "type": "object" },
                  "capabilities": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                }
              },
              "ChatRequest": {
                "type": "object",
                "required": ["model", "messages"],
                "properties": {
                  "model": { "type": "string" },
                  "messages": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "role": { "type": "string", "enum": ["system", "user", "assistant", "tool"] },
                        "content": { "type": "string" },
                        "images": { "type": "array", "items": { "type": "string" }, "description": "Base64-encoded images (vision models)" },
                        "tool_calls": { "type": "array", "items": { "type": "object" } }
                      }
                    }
                  },
                  "stream": { "type": "boolean", "default": true },
                  "format": { "type": "string" },
                  "options": {
                    "type": "object",
                    "properties": {
                      "temperature": { "type": "number" },
                      "repeat_penalty": { "type": "number" },
                      "num_predict": { "type": "integer" }
                    }
                  },
                  "tools": { "type": "array", "items": { "type": "object" } }
                }
              },
              "GenerateRequest": {
                "type": "object",
                "required": ["model", "prompt"],
                "properties": {
                  "model": { "type": "string" },
                  "prompt": { "type": "string" },
                  "system": { "type": "string" },
                  "stream": { "type": "boolean", "default": true },
                  "format": { "type": "string" },
                  "options": {
                    "type": "object",
                    "properties": {
                      "temperature": { "type": "number" },
                      "repeat_penalty": { "type": "number" }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static void FillTokenStats(RequestLog log, LlamaCppStreamChunk? chunk)
    {
        FillTokenStats(log, chunk?.Usage);

        LlamaCppTimings? timings = chunk?.Timings;
        if (timings is not null)
        {
            log.DraftN = timings.DraftN;
            log.DraftNAccepted = timings.DraftNAccepted;
        }
    }

    private static void FillTokenStats(RequestLog log, LlamaCppUsage? usage)
    {
        if (usage is null) return;
        log.PromptTokens = usage.PromptTokens;
        log.CompletionTokens = usage.CompletionTokens;
        log.TotalTokens = usage.TotalTokens;
        log.CachedPromptTokens = usage.PromptTokensDetails?.CachedTokens ?? 0;
        log.ReasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens ?? 0;
    }

    /// <summary>Parses a complete (non-streaming) chat/completion JSON body for its chunk (usage + timings); null when absent or unparseable.</summary>
    private static LlamaCppStreamChunk? TryParseChunk(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<LlamaCppStreamChunk>(body, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Wraps a write-only stream and counts the bytes written through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            BytesWritten += buffer.Length;
        }
    }

    /// <summary>Captures bytes written through it while forwarding them immediately to the inner stream.</summary>
    private sealed class ResponseCaptureStream(Stream inner) : Stream
    {
        private readonly MemoryStream _capture = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public string GetCapturedText() => Encoding.UTF8.GetString(_capture.ToArray());

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _capture.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            await _capture.WriteAsync(buffer.AsMemory(offset, count), ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await _capture.WriteAsync(buffer, ct);
        }
    }

    private sealed class PeriodicHeartbeatState : IDisposable
    {
        private readonly Func<ModelMapping, CancellationToken, Task> _sendHeartbeatAsync;
        private readonly Action<ModelMapping, string> _recordFailure;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly System.Threading.Timer _timer;
        private ModelMapping _mapping;

        public PeriodicHeartbeatState(
            ModelMapping mapping,
            int intervalSeconds,
            Func<ModelMapping, CancellationToken, Task> sendHeartbeatAsync,
            Action<ModelMapping, string> recordFailure)
        {
            _mapping = CloneMapping(mapping);
            _sendHeartbeatAsync = sendHeartbeatAsync;
            _recordFailure = recordFailure;
            TimeSpan interval = GetInterval(intervalSeconds);
            _timer = new System.Threading.Timer(OnTimer, null, TimeSpan.Zero, interval);
        }

        public void Update(ModelMapping mapping, int intervalSeconds)
        {
            _mapping = CloneMapping(mapping);
            TimeSpan interval = GetInterval(intervalSeconds);
            _timer.Change(TimeSpan.Zero, interval);
        }

        private void OnTimer(object? state)
        {
            if (_cts.IsCancellationRequested)
                return;

            _ = SendAsync();
        }

        private async Task SendAsync()
        {
            try
            {
                if (!await _sendLock.WaitAsync(0, _cts.Token).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _sendHeartbeatAsync(_mapping, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _recordFailure(_mapping, ex.Message);

                // Logged at Debug and tagged, not at Warning with the exception attached. A ping fires
                // on a timer for every enabled model, so an upstream that is down â€” or a model that is
                // deliberately not running â€” produced a full stack trace every interval forever, which
                // made up the large majority of the application log and could roll a crash report out
                // of existence before anyone read it.
                //
                // Gatewayed by the logger-wide exclusion on LoggingSettings.HeartbeatMinimumLevel, so
                // raising that threshold silences these everywhere at once. Nothing is lost: the last
                // status, failure count, probe time and error are recorded per model and shown on the
                // Heartbeats tab. The message marker additionally lets the System Logs tab isolate or
                // hide these lines.
                Log.ForContext(AppLogger.HeartbeatMarkerProperty, true)
                    .Debug(ex, AppLogger.HeartbeatMessage("Periodic heartbeat failed for model {Model}"), _mapping.ProxyName);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _timer.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }

        // Clamped to the same bounds AppSettings.Normalize enforces, so a stale or hand-edited value
        // can never produce a timer that fires faster than the UI allows.
        private static TimeSpan GetInterval(int intervalSeconds)
            => AppSettings.HeartbeatInterval(intervalSeconds);

        private static ModelMapping CloneMapping(ModelMapping mapping) => new()
        {
            IsEnabled = mapping.IsEnabled,
            ProxyName = mapping.ProxyName,
            ModelName = mapping.ModelName,
            EnableThinkingCompatibility = mapping.EnableThinkingCompatibility,
            EnableHeartbeats = mapping.EnableHeartbeats,
            CredentialName = mapping.CredentialName,
            UpstreamType = mapping.UpstreamType,
            UpstreamUrl = mapping.UpstreamUrl,
            UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds,
            RepeatPenalty = mapping.RepeatPenalty,
            TemperaturePriority = mapping.TemperaturePriority,
            RepeatPenaltyPriority = mapping.RepeatPenaltyPriority,
            Temperature = mapping.Temperature,
            InstructionSetName = mapping.InstructionSetName,
            RedactRequestBodies = mapping.RedactRequestBodies,
            RedactResponseBodies = mapping.RedactResponseBodies,
            RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields,
        };
    }
}
