using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the contract of <see cref="AppDatabase.QueryTokenUsage"/>, the aggregate behind the
/// dashboard Usage view: (local day, model, user) grouping, token-bearing-only counting, the
/// prompt+completion fallback when the upstream reported no total, per-user narrowing, and the
/// inclusive-from / exclusive-to UTC range.
/// </summary>
public class TokenUsageQueryTests
{
    private static readonly DateTime Day1 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Day2 = Day1.AddDays(1);

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-usage-{Guid.NewGuid():N}.db");

    private static AppDatabase Open(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    private static RequestLog Row(DateTime timestamp, string model, int? userId,
        int prompt = 0, int completion = 0, int total = 0, int cached = 0, int reasoning = 0) =>
        new()
        {
            Timestamp = timestamp,
            Method = "POST",
            OllamaPath = "/v1/chat/completions",
            Model = model,
            StatusCode = 200,
            Status = RequestStatus.Success,
            UserId = userId,
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
            CachedPromptTokens = cached,
            ReasoningTokens = reasoning,
        };

    private static IReadOnlyList<TokenUsageRow> QueryAll(AppDatabase database) =>
        database.QueryTokenUsage(Day1.ToUniversalTime(), Day2.AddDays(2).ToUniversalTime());

    [Fact]
    public void GroupsByDayModelAndUser_AndSumsTokens()
    {
        string path = NewDatabasePath();
        using AppDatabase database = Open(path);
        database.Insert(Row(Day1, "alpha", 1, prompt: 10, completion: 4, total: 14, cached: 3, reasoning: 2));
        database.Insert(Row(Day1.AddHours(2), "alpha", 1, prompt: 20, completion: 6, total: 26, cached: 1, reasoning: 1));
        database.Insert(Row(Day1, "beta", 1, prompt: 5, completion: 1, total: 6));
        database.Insert(Row(Day1, "alpha", 2, prompt: 7, completion: 2, total: 9));
        database.Insert(Row(Day2, "alpha", 1, prompt: 1, completion: 1, total: 2));

        IReadOnlyList<TokenUsageRow> rows = QueryAll(database);

        Assert.Equal(4, rows.Count);
        TokenUsageRow merged = rows.Single(r => r.Day == Day1.ToString("yyyy-MM-dd") && r.Model == "alpha" && r.UserId == 1);
        Assert.Equal(2, merged.Requests);
        Assert.Equal(30, merged.PromptTokens);
        Assert.Equal(10, merged.CompletionTokens);
        Assert.Equal(40, merged.TotalTokens);
        Assert.Equal(4, merged.CachedPromptTokens);
        Assert.Equal(3, merged.ReasoningTokens);
        Assert.Contains(rows, r => r.Day == Day2.ToString("yyyy-MM-dd") && r.Model == "alpha");
    }

    [Fact]
    public void ExcludesRowsWithoutAnyTokens()
    {
        string path = NewDatabasePath();
        using AppDatabase database = Open(path);
        database.Insert(Row(Day1, "probe", 1)); // zero-token probe: not usage
        database.Insert(Row(Day1, "alpha", 1, prompt: 8));

        TokenUsageRow row = Assert.Single(QueryAll(database));
        Assert.Equal("alpha", row.Model);
        Assert.Equal(8, row.PromptTokens);
    }

    [Fact]
    public void TotalFallsBackToPromptPlusCompletion_WhenUpstreamReportedNoTotal()
    {
        string path = NewDatabasePath();
        using AppDatabase database = Open(path);
        database.Insert(Row(Day1, "no-total", 1, prompt: 10, completion: 4));
        database.Insert(Row(Day1, "with-total", 1, prompt: 10, completion: 4, total: 99));

        IReadOnlyList<TokenUsageRow> rows = QueryAll(database);

        Assert.Equal(14, rows.Single(r => r.Model == "no-total").TotalTokens);
        Assert.Equal(99, rows.Single(r => r.Model == "with-total").TotalTokens);
    }

    [Fact]
    public void UserIdFilterNarrowsToOneUser_NullIncludesUnattributed()
    {
        string path = NewDatabasePath();
        using AppDatabase database = Open(path);
        database.Insert(Row(Day1, "alpha", 1, prompt: 10, completion: 1, total: 11));
        database.Insert(Row(Day1, "alpha", 2, prompt: 20, completion: 2, total: 22));
        database.Insert(Row(Day1, "alpha", null, prompt: 30, completion: 3, total: 33));

        TokenUsageRow filtered = Assert.Single(database.QueryTokenUsage(
            Day1.ToUniversalTime(), Day2.ToUniversalTime(), userId: 1));
        Assert.Equal(1, filtered.UserId);
        Assert.Equal(11, filtered.TotalTokens);

        IReadOnlyList<TokenUsageRow> all = database.QueryTokenUsage(Day1.ToUniversalTime(), Day2.ToUniversalTime());
        Assert.Equal(3, all.Count);
        Assert.Contains(all, r => r.UserId is null && r.TotalTokens == 33);
    }

    [Fact]
    public void RangeIsInclusiveFrom_ExclusiveTo()
    {
        string path = NewDatabasePath();
        using AppDatabase database = Open(path);
        DateTime fromLocal = Day1;
        DateTime toLocal = Day2;
        database.Insert(Row(fromLocal.AddSeconds(-1), "before", 1, prompt: 1, total: 1));
        database.Insert(Row(fromLocal, "at-from", 1, prompt: 2, total: 2));
        database.Insert(Row(toLocal, "at-to", 1, prompt: 3, total: 3));

        IReadOnlyList<TokenUsageRow> rows = database.QueryTokenUsage(
            fromLocal.ToUniversalTime(), toLocal.ToUniversalTime());

        TokenUsageRow row = Assert.Single(rows);
        Assert.Equal("at-from", row.Model);
    }
}