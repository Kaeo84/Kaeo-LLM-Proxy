using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.Core.Models;

// ─────────────────────────── Tool / function calling ──────────────────────

internal sealed class OllamaToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public object? Parameters { get; set; }
}

internal sealed class OllamaTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public OllamaToolFunction? Function { get; set; }
}

internal sealed class OllamaToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public object? Arguments { get; set; }
}

internal sealed class OllamaToolCall
{
    /// <summary>Optional id returned by upstream so we can correlate the
    /// subsequent role:"tool" reply back to its originating call.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("function")] public OllamaToolCallFunction? Function { get; set; }
}

// ─────────────────────────── Shared message ───────────────────────────────

/// <summary>
/// Ollama message. Content may be null when the model returns a tool_calls delta.
/// </summary>
internal sealed class OllamaMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<OllamaToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

    [JsonConstructor]
    public OllamaMessage() { }

    public OllamaMessage(string role, string? content) { Role = role; Content = content; }
}

// ─────────────────────────── /api/generate ────────────────────────────────

internal sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    [JsonPropertyName("system")] public string? System { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("format")] public object? Format { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaGenerateResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("response")] public string Response { get; set; } = string.Empty;
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
    [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
    [JsonPropertyName("eval_count")] public int? EvalCount { get; set; }
    [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
    [JsonPropertyName("eval_duration")] public long? EvalDuration { get; set; }
}

// ─────────────────────────── /api/chat ────────────────────────────────────

internal sealed class OllamaChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("tools")] public List<OllamaTool>? Tools { get; set; }
    [JsonPropertyName("format")] public object? Format { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaChatResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
    [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
    [JsonPropertyName("eval_count")] public int? EvalCount { get; set; }
    [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
    [JsonPropertyName("eval_duration")] public long? EvalDuration { get; set; }
}

// ─────────────────────────── /api/tags ────────────────────────

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaModelEntry> Models { get; set; } = [];
}

internal sealed class OllamaModelEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string Digest { get; set; } = string.Empty;
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; set; }

    /// <summary>Ollama-style capability tokens (e.g. completion, tools, vision) advertised for this model.</summary>
    [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
}

internal sealed class OllamaModelDetails
{
    [JsonPropertyName("parent_model")] public string ParentModel { get; set; } = string.Empty;
    [JsonPropertyName("format")] public string Format { get; set; } = "gguf";
    [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
    /// <summary>Real Ollama clients read the families[] array as well as family.</summary>
    [JsonPropertyName("families")] public List<string>? Families { get; set; }
    [JsonPropertyName("parameter_size")] public string ParameterSize { get; set; } = string.Empty;
    [JsonPropertyName("quantization_level")] public string QuantizationLevel { get; set; } = string.Empty;
}

// ─────────────────────────── /api/show ────────────────────────

internal sealed class OllamaShowRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class OllamaShowResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; set; }
    /// <summary>Real Ollama uses snake_case "model_info"; clients depend on that name.</summary>
    [JsonPropertyName("model_info")] public Dictionary<string, object>? ModelInfo { get; set; }
    [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
}

// ─────────────────────────── /api/embeddings ──────────────────────────────

internal sealed class OllamaEmbeddingsRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    /// <summary>Legacy single-string form (Ollama &lt; 0.3).</summary>
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    /// <summary>New batch form: string or string[] (Ollama >= 0.3).</summary>
    [JsonPropertyName("input")] public object? Input { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaEmbeddingsResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    /// <summary>Single-vector legacy response.</summary>
    [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    /// <summary>Multi-vector batch response.</summary>
    [JsonPropertyName("embeddings")] public List<float[]>? Embeddings { get; set; }
    [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
    [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
}

// ─────────────────────────── Options ──────────────────────────

/// <summary>
/// Generation options shared between /api/generate and /api/chat.
/// Fields map to llama.cpp OpenAI-compatible parameters.
/// Reference: lms-shared-types/LLMPredictionConfig.ts
/// </summary>
internal sealed class OllamaOptions
{
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("top_k")] public int? TopK { get; set; }
    [JsonPropertyName("min_p")] public float? MinP { get; set; }
    [JsonPropertyName("repeat_penalty")] public float? RepeatPenalty { get; set; }
    [JsonPropertyName("num_predict")] public int? NumPredict { get; set; }
    [JsonPropertyName("stop")] public List<string>? Stop { get; set; }
    [JsonPropertyName("seed")] public int? Seed { get; set; }
    [JsonPropertyName("num_ctx")] public int? NumCtx { get; set; }
    [JsonPropertyName("presence_penalty")] public float? PresencePenalty { get; set; }
    [JsonPropertyName("frequency_penalty")] public float? FrequencyPenalty { get; set; }
    [JsonPropertyName("tfs_z")] public float? TfsZ { get; set; }
    [JsonPropertyName("mirostat")] public int? Mirostat { get; set; }
    [JsonPropertyName("mirostat_tau")] public float? MirostatTau { get; set; }
    [JsonPropertyName("mirostat_eta")] public float? MirostatEta { get; set; }
}

// ─────────────────────────── llama.cpp / OpenAI types ─────────────────────

internal sealed class LlamaCppModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "llama.cpp";
}

internal sealed class LlamaCppModelsResponse
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public List<LlamaCppModel> Data { get; set; } = [];
}

internal sealed class LlamaCppToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public object? Parameters { get; set; }
}

internal sealed class LlamaCppTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public LlamaCppToolFunction? Function { get; set; }
}

internal sealed class LlamaCppToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

internal sealed class LlamaCppToolCall
{
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public LlamaCppToolCallFunction? Function { get; set; }
}

internal sealed class LlamaCppResponseFormat
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    /// <summary>Used when Type == "json_schema" to pass a strict OpenAI JSON-schema object.</summary>
    [JsonPropertyName("json_schema")] public object? JsonSchema { get; set; }
}

/// <summary>OpenAI-compatible message used in requests to llama.cpp.</summary>
internal sealed class LlamaCppMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<LlamaCppToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

    [JsonConstructor]
    public LlamaCppMessage() { }

    public LlamaCppMessage(string role, string? content) { Role = role; Content = content; }
}

internal sealed class LlamaCppChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("messages")] public List<LlamaCppMessage> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("tools")] public List<LlamaCppTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
    [JsonPropertyName("response_format")] public LlamaCppResponseFormat? ResponseFormat { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("top_k")] public int? TopK { get; set; }
    [JsonPropertyName("min_p")] public float? MinP { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("stop")] public List<string>? Stop { get; set; }
    [JsonPropertyName("seed")] public int? Seed { get; set; }
    [JsonPropertyName("presence_penalty")] public float? PresencePenalty { get; set; }
    [JsonPropertyName("frequency_penalty")] public float? FrequencyPenalty { get; set; }
    [JsonPropertyName("repeat_penalty")] public float? RepeatPenalty { get; set; }
    [JsonPropertyName("mirostat")] public int? Mirostat { get; set; }
    [JsonPropertyName("mirostat_tau")] public float? MirostatTau { get; set; }
    [JsonPropertyName("mirostat_eta")] public float? MirostatEta { get; set; }
    /// <summary>Forwarded to llama.cpp-compatible upstreams that accept it (mapped from num_ctx).</summary>
    [JsonPropertyName("n_ctx")] public int? NCtx { get; set; }
    /// <summary>Non-standard reasoning intensity hint supported by some hosted providers.</summary>
    [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
    /// <summary>Modern nested reasoning object (<c>reasoning.effort</c>) used by newer OpenAI models.</summary>
    [JsonPropertyName("reasoning")] public LlamaCppReasoning? Reasoning { get; set; }
    /// <summary>Qwen Cloud thinking switch sent alongside <c>reasoning_effort</c>.</summary>
    [JsonPropertyName("enable_thinking")] public bool? EnableThinking { get; set; }
}

/// <summary>Modern OpenAI reasoning object carrying the effort level (e.g. gpt-5.5).</summary>
internal sealed class LlamaCppReasoning
{
    [JsonPropertyName("effort")] public string? Effort { get; set; }
}

internal sealed class LlamaCppCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("response_format")] public LlamaCppResponseFormat? ResponseFormat { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("top_k")] public int? TopK { get; set; }
    [JsonPropertyName("min_p")] public float? MinP { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("stop")] public List<string>? Stop { get; set; }
    [JsonPropertyName("seed")] public int? Seed { get; set; }
    [JsonPropertyName("presence_penalty")] public float? PresencePenalty { get; set; }
    [JsonPropertyName("frequency_penalty")] public float? FrequencyPenalty { get; set; }
    [JsonPropertyName("repeat_penalty")] public float? RepeatPenalty { get; set; }
}

internal sealed class LlamaCppStreamChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("object")] public string Object { get; set; } = string.Empty;
    [JsonPropertyName("choices")] public List<LlamaCppChoice> Choices { get; set; } = [];
    [JsonPropertyName("usage")] public LlamaCppUsage? Usage { get; set; }
}

internal sealed class LlamaCppChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public LlamaCppDelta? Delta { get; set; }
    /// <summary>Present on non-streaming /v1/chat/completions choices.</summary>
    [JsonPropertyName("message")] public LlamaCppDelta? Message { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal sealed class LlamaCppDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<LlamaCppToolCall>? ToolCalls { get; set; }
}

internal sealed class LlamaCppUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    [JsonPropertyName("prompt_tokens_details")] public LlamaCppPromptTokensDetails? PromptTokensDetails { get; set; }
    [JsonPropertyName("completion_tokens_details")] public LlamaCppCompletionTokensDetails? CompletionTokensDetails { get; set; }
}

/// <summary>OpenAI-style prompt token breakdown (e.g. Qwen Cloud reports cached/text tokens).</summary>
internal sealed class LlamaCppPromptTokensDetails
{
    [JsonPropertyName("cached_tokens")] public int CachedTokens { get; set; }
    [JsonPropertyName("text_tokens")] public int TextTokens { get; set; }
}

/// <summary>OpenAI-style completion token breakdown (e.g. Qwen Cloud reports reasoning/text tokens).</summary>
internal sealed class LlamaCppCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")] public int ReasoningTokens { get; set; }
    [JsonPropertyName("text_tokens")] public int TextTokens { get; set; }
}

internal sealed class LlamaCppEmbeddingsRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    /// <summary>Single string or array of strings.</summary>
    [JsonPropertyName("input")] public object Input { get; set; } = string.Empty;
}

internal sealed class LlamaCppEmbeddingsResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("data")] public List<LlamaCppEmbeddingData> Data { get; set; } = [];
    [JsonPropertyName("usage")] public LlamaCppUsage? Usage { get; set; }
}

internal sealed class LlamaCppEmbeddingData
{
    [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = [];
}

// ─────────────────────────── Error responses ──────────────────────────────

/// <summary>Standard error response from llama.cpp / OpenAI-compatible endpoints.</summary>
internal sealed class LlamaCppErrorResponse
{
    [JsonPropertyName("error")] public LlamaCppErrorDetail? Error { get; set; }
}

internal sealed class LlamaCppErrorDetail
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("code")] public int? Code { get; set; }
}

