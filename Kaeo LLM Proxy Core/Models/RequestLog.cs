namespace Kaeo.LlmProxy.Core.Models;

internal enum RequestStatus
{
    Success,
    Error,
    Cancelled,
}

/// <summary>Identifies which service produced a request log entry.</summary>
internal enum LogSource
{
    Proxy,
    Mcp,
}

/// <summary>A single logged proxy request with timing and token stats.</summary>
internal sealed class RequestLog
{
    /// <summary>
    /// Unique correlation ID assigned when the request is received. Surfaced in server logs
    /// (via Serilog LogContext) and in error responses so a client-reported failure can be
    /// correlated with the exact server-side request.
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Method { get; set; } = string.Empty;
    public string OllamaPath { get; set; } = string.Empty;
    public string UpstreamPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The model name as sent by the client before any proxy-side redirect (e.g. compact-model
    /// substitution). When non-empty and different from <see cref="Model"/>, indicates the proxy
    /// rewrote the model. Empty when no redirect occurred.
    /// </summary>
    public string OriginalModel { get; set; } = string.Empty;
    public bool Streaming { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Success;
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double TokensPerSecond { get; set; }

    /// <summary>
    /// Total tokens reported by the upstream <c>usage</c> block (prompt + completion).
    /// Zero when the upstream did not report usage.
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Prompt tokens served from cache (<c>usage.prompt_tokens_details.cached_tokens</c>).
    /// Zero when the upstream did not report the detail block.
    /// </summary>
    public int CachedPromptTokens { get; set; }

    /// <summary>
    /// Completion tokens spent on reasoning (<c>usage.completion_tokens_details.reasoning_tokens</c>).
    /// Zero when the upstream did not report the detail block.
    /// </summary>
    public int ReasoningTokens { get; set; }

    /// <summary>
    /// Number of draft tokens proposed (<c>timings.draft_n</c>). Zero when no draft was used.
    /// </summary>
    public int DraftN { get; set; }

    /// <summary>
    /// Number of draft tokens accepted by the target model (<c>timings.draft_n_accepted</c>).
    /// Zero when no draft was used.
    /// </summary>
    public int DraftNAccepted { get; set; }

    /// <summary>
    /// When set, references the <see cref="ExceptionDetail.Id"/> stored in the exceptions
    /// collection for the full stack trace and inner exception chain.
    /// </summary>
    public int? ExceptionId { get; set; }

    /// <summary>
    /// The request body received from the client, exactly as it was sent on the wire — no
    /// proxy transformations are applied before this capture (the proxy makes no changes at
    /// all until the upstream request body). Captured when <c>CollectRequestDetails</c> is
    /// enabled in settings; the only alteration is redaction of sensitive fields (or a whole-body
    /// marker) when those options are enabled. Null when capture is disabled.
    /// </summary>
    public string? RequestBody { get; set; }

    /// <summary>
    /// The request body actually sent to the upstream after proxy translation/rewriting,
    /// captured when <c>CollectRequestDetails</c> is enabled. For translated Ollama requests
    /// this is the OpenAI-compatible body built by the proxy; for OpenAI passthrough it is the
    /// rewritten client body. Values the proxy injects per-model (e.g. <c>reasoning_effort</c>)
    /// are visible here, allowing before/after comparison against <see cref="RequestBody"/>.
    /// Null when capture is disabled or no upstream call was made.
    /// </summary>
    public string? UpstreamRequestBody { get; set; }

    /// <summary>
    /// The Ollama-formatted response body actually sent back to the client, captured when
    /// <c>CollectResponseDetails</c> is enabled in settings. This is the "after" side of the
    /// OpenAI→Ollama response translation: for non-streaming responses it is the serialized
    /// <c>OllamaChatResponse</c>/<c>OllamaGenerateResponse</c> JSON; for streaming responses it is
    /// the NDJSON stream of Ollama chunks. Null when capture is disabled.
    /// </summary>
    public string? ResponseBody { get; set; }

    /// <summary>
    /// The raw upstream (OpenAI-compatible) response body, captured when <c>DebugMode</c> is
    /// enabled. This is the "before" side of the response translation: what the upstream
    /// actually returned, before the proxy converted it into the Ollama format stored in
    /// <see cref="ResponseBody"/> (the "after"). For streaming responses this accumulates the
    /// raw upstream SSE <c>data:</c> lines. Null when <c>DebugMode</c> is disabled.
    /// </summary>
    public string? UpstreamResponseBody { get; set; }

    /// <summary>
    /// The finish/stop reason reported by the upstream for this response (e.g. "stop",
    /// "length", "tool_calls", "content_filter"). Helps diagnose why the model stopped
    /// mid-task. Null when the upstream did not report a finish reason.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// Plain multi-line text listing every settings-driven override/transformation the proxy
    /// applied for this request (temperature, repeat_penalty, instruction-set injection,
    /// reasoning effort, model rewrite), each marked as injected / replaced / passed through /
    /// omitted. Captured when <c>DebugMode</c> is enabled and rendered at the top of the log
    /// details dialog. Null when <c>DebugMode</c> is disabled.
    /// </summary>
    public string? DebugSummary { get; set; }

    /// <summary>Size of the inbound request body in bytes. Zero when there is no body.</summary>
    public long RequestBytes { get; set; }

    /// <summary>Size of the outbound response body in bytes. -1 when unknown.</summary>
    public long ResponseBytes { get; set; }

    /// <summary>
    /// Derives the <see cref="RequestStatus"/> that corresponds to an HTTP status code, so every
    /// logging path shares one rule instead of each branch remembering to set
    /// <see cref="RequestLog.Status"/> alongside <see cref="RequestLog.StatusCode"/>.
    /// </summary>
    /// <remarks>
    /// 499 is the nginx convention for a client that closed the connection before the response
    /// completed, which the proxy reports for a cancelled request. It maps to
    /// <see cref="RequestStatus.Cancelled"/> rather than <see cref="RequestStatus.Error"/> so a
    /// user abandoning a request is not counted against the upstream's reliability.
    /// <para>
    /// A code of zero means no response was recorded at all — the request never reached a handler
    /// that could answer it. That is treated as an error rather than a success, because
    /// <see cref="RequestStatus"/> defaults to <see cref="RequestStatus.Success"/> and an
    /// unanswered request silently appearing successful is what made undeliverable errors
    /// invisible in the log.
    /// </para>
    /// </remarks>
    internal static RequestStatus DeriveStatus(int statusCode) => statusCode switch
    {
        499 => RequestStatus.Cancelled,
        >= 400 => RequestStatus.Error,
        0 => RequestStatus.Error,
        _ => RequestStatus.Success,
    };
}
