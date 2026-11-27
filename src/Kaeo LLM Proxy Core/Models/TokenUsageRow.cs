namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// One (local day, model, user) aggregate of token-bearing proxy requests, produced by
/// <c>AppDatabase.QueryTokenUsage</c> for the dashboard's Usage view. Days are server-local
/// calendar days (<c>date(timestamp_utc, 'localtime')</c>); <see cref="UserId"/> is null for
/// requests that carried no known API key (open proxy, or rows written before attribution
/// existed). <see cref="TotalTokens"/> falls back to prompt + completion when the upstream
/// did not report a usage total.
/// </summary>
internal sealed record TokenUsageRow(
    string Day,
    string Model,
    int? UserId,
    int Requests,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long CachedPromptTokens,
    long ReasoningTokens);
