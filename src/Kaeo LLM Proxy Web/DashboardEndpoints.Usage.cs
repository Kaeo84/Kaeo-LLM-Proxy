using System.Globalization;
using System.Security.Claims;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Token usage reporting for the dashboard's Usage tab. Read Only sessions always see their own
/// consumption; Admin sessions can scope to themselves, one user, or everyone (including
/// unattributed traffic from before API-key attribution existed). Data comes from the request
/// log database, so it covers history rather than just the in-memory window.
/// </summary>
internal static partial class DashboardEndpoints
{
    private const int MaxUsageRangeDays = 366;

    private static void MapUsage(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/usage", (HttpContext context, string? from, string? to, string? groupBy, string? userId, string? model) =>
        {
            DateTime today = DateTime.Today;
            // Defaults to the current week (Monday-first, matching the week buckets below) because
            // that is what people check first; longer ranges are one dropdown change away.
            DateTime rangeFrom = StartOfWeek(today);
            DateTime rangeTo = today;
            if (from is not null && !TryParseDay(from, out rangeFrom))
                return Results.Json(new { error = "from must be a date in yyyy-MM-dd format." }, statusCode: StatusCodes.Status400BadRequest);
            if (to is not null && !TryParseDay(to, out rangeTo))
                return Results.Json(new { error = "to must be a date in yyyy-MM-dd format." }, statusCode: StatusCodes.Status400BadRequest);
            if (rangeFrom > rangeTo)
                return Results.Json(new { error = "from must not be after to." }, statusCode: StatusCodes.Status400BadRequest);
            if ((rangeTo - rangeFrom).TotalDays > MaxUsageRangeDays)
                return Results.Json(new { error = $"The usage range is limited to {MaxUsageRangeDays} days." }, statusCode: StatusCodes.Status400BadRequest);

            groupBy = string.IsNullOrWhiteSpace(groupBy) ? "day" : groupBy.ToLowerInvariant();
            if (groupBy is not ("day" or "week" or "month"))
                return Results.Json(new { error = "groupBy must be day, week, or month." }, statusCode: StatusCodes.Status400BadRequest);

            bool fullVisibility = DashboardAuth.IsAdmin(context);
            int? callerId = int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int parsedId)
                ? parsedId
                : null;

            // Non-Admin roles are pinned to their own user no matter what was requested.
            int? filterUserId;
            string scope;
            if (!fullVisibility)
            {
                if (callerId is null)
                    return Results.Json(new { error = "Sign in with a dashboard username and password." }, statusCode: StatusCodes.Status401Unauthorized);
                filterUserId = callerId;
                scope = "self";
            }
            else if (string.IsNullOrWhiteSpace(userId) || userId.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                filterUserId = null;
                scope = "all";
            }
            else if (userId.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                filterUserId = callerId;
                scope = callerId.HasValue ? "self" : "all";
            }
            else if (int.TryParse(userId, out int targetId) && targetId > 0)
            {
                filterUserId = targetId;
                scope = "user";
            }
            else
            {
                return Results.Json(new { error = "userId must be 'all', 'me', or a numeric user id." }, statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                IReadOnlyList<TokenUsageRow> rows = controller.Database.QueryTokenUsage(
                    DateTime.SpecifyKind(rangeFrom, DateTimeKind.Local),
                    DateTime.SpecifyKind(rangeTo.AddDays(1), DateTimeKind.Local),
                    filterUserId,
                    string.IsNullOrWhiteSpace(model) ? null : model);

                List<ProxyUser> users = fullVisibility ? [.. controller.Database.LoadProxyUsers()] : [];
                Dictionary<int, string> userNames = users.ToDictionary(u => u.Id, u => u.Name);

                return Results.Json(new
                {
                    from = rangeFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    to = rangeTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    groupBy,
                    scope,
                    users = users.Select(u => new { id = u.Id, name = u.Name }).ToList(),
                    models = rows.GroupBy(r => r.Model)
                        .OrderByDescending(g => g.Sum(r => r.TotalTokens))
                        .Select(g => g.Key)
                        .ToList(),
                    buckets = BuildBuckets(rows, rangeFrom, rangeTo, groupBy),
                    days = BuildDays(rows),
                    byUser = rows.GroupBy(r => r.UserId)
                        .Select(g => new
                        {
                            userId = g.Key,
                            name = g.Key is { } uid && userNames.TryGetValue(uid, out string? name) ? name : "Unattributed",
                            requests = g.Sum(r => r.Requests),
                            promptTokens = g.Sum(r => r.PromptTokens),
                            completionTokens = g.Sum(r => r.CompletionTokens),
                            totalTokens = g.Sum(r => r.TotalTokens),
                        })
                        .OrderByDescending(u => u.totalTokens)
                        .ToList(),
                    totals = new
                    {
                        requests = rows.Sum(r => r.Requests),
                        promptTokens = rows.Sum(r => r.PromptTokens),
                        completionTokens = rows.Sum(r => r.CompletionTokens),
                        totalTokens = rows.Sum(r => r.TotalTokens),
                    },
                });
            }
            catch (Exception ex) when (ex is IOException or SqliteException)
            {
                Serilog.Log.Warning(ex, "Usage query failed");
                return Results.Json(new { error = "Usage data is temporarily unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static bool TryParseDay(string text, out DateTime day) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>Monday of the week containing <paramref name="day"/> (weeks are Monday-first here).</summary>
    private static DateTime StartOfWeek(DateTime day) =>
        day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static object BuildDays(IReadOnlyList<TokenUsageRow> rows) =>
        rows.GroupBy(r => r.Day)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                day = g.Key,
                requests = g.Sum(r => r.Requests),
                promptTokens = g.Sum(r => r.PromptTokens),
                completionTokens = g.Sum(r => r.CompletionTokens),
                totalTokens = g.Sum(r => r.TotalTokens),
                models = g.GroupBy(r => r.Model)
                    .Select(mg => new
                    {
                        model = mg.Key,
                        requests = mg.Sum(r => r.Requests),
                        promptTokens = mg.Sum(r => r.PromptTokens),
                        completionTokens = mg.Sum(r => r.CompletionTokens),
                        totalTokens = mg.Sum(r => r.TotalTokens),
                        cachedPromptTokens = mg.Sum(r => r.CachedPromptTokens),
                        reasoningTokens = mg.Sum(r => r.ReasoningTokens),
                    })
                    .OrderByDescending(m => m.totalTokens)
                    .ToList(),
            })
            .ToList();

    /// <summary>
    /// Buckets the rows into day/week/month spans covering the whole requested range (empty
    /// spans included, so the chart axis is continuous). Weeks start on Monday.
    /// </summary>
    private static object BuildBuckets(IReadOnlyList<TokenUsageRow> rows, DateTime from, DateTime to, string groupBy)
    {
        List<(string Key, string Label, DateTime Start, DateTime End)> spans = [];
        if (groupBy == "month")
        {
            for (DateTime cur = new(from.Year, from.Month, 1); cur <= to; cur = cur.AddMonths(1))
                spans.Add((cur.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    cur.ToString("MMM yyyy", CultureInfo.InvariantCulture), cur, cur.AddMonths(1)));
        }
        else if (groupBy == "week")
        {
            DateTime monday = StartOfWeek(from);
            for (DateTime cur = monday; cur <= to; cur = cur.AddDays(7))
            {
                DateTime end = cur.AddDays(7);
                spans.Add((cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    cur.ToString("MMM d", CultureInfo.InvariantCulture) + " – " + end.AddDays(-1).ToString("MMM d", CultureInfo.InvariantCulture),
                    cur, end));
            }
        }
        else
        {
            for (DateTime cur = from; cur <= to; cur = cur.AddDays(1))
                spans.Add((cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    cur.ToString("MMM d", CultureInfo.InvariantCulture), cur, cur.AddDays(1)));
        }

        // Assign each row to its span once. A day→span map is built from the spans themselves, so
        // the lookup is O(1) per row instead of scanning every span for every row (which made a
        // 366-day range cost rows × spans string parses).
        Dictionary<string, List<TokenUsageRow>> bySpan = spans.ToDictionary(s => s.Key, _ => new List<TokenUsageRow>());
        Dictionary<string, string> spanKeyByDay = new(StringComparer.Ordinal);
        foreach ((string key, _, DateTime start, DateTime end) in spans)
            for (DateTime cur = start; cur < end; cur = cur.AddDays(1))
                spanKeyByDay[cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = key;

        // Rows outside the range cannot occur (SQL filtered), so a miss is simply skipped.
        foreach (TokenUsageRow row in rows)
            if (spanKeyByDay.TryGetValue(row.Day, out string? spanKey))
                bySpan[spanKey].Add(row);

        return spans.Select(s =>
        {
            List<TokenUsageRow> bucketRows = bySpan[s.Key];
            return new
            {
                key = s.Key,
                label = s.Label,
                requests = bucketRows.Sum(r => r.Requests),
                promptTokens = bucketRows.Sum(r => r.PromptTokens),
                completionTokens = bucketRows.Sum(r => r.CompletionTokens),
                totalTokens = bucketRows.Sum(r => r.TotalTokens),
                byModel = bucketRows.GroupBy(r => r.Model)
                    .Select(g => new
                    {
                        model = g.Key,
                        requests = g.Sum(r => r.Requests),
                        promptTokens = g.Sum(r => r.PromptTokens),
                        completionTokens = g.Sum(r => r.CompletionTokens),
                        totalTokens = g.Sum(r => r.TotalTokens),
                    })
                    .OrderByDescending(m => m.totalTokens)
                    .ToList(),
            };
        }).ToList();
    }
}
