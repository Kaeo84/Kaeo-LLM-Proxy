using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// System-log and live-stream endpoints — the two observability views the Windows GUI has. Both
/// stay Admin-only: the log store can contain payload text and the stream monitor exposes the raw
/// bytes of other users' in-flight requests. The auth middleware rejects sessions that lack the
/// capability a route needs.
/// </summary>
internal static partial class DashboardEndpoints
{
    private const int SystemLogDefaultLimit = 500;

    private static void MapObservability(WebApplication app, ProxyHostController controller)
    {
        // Newest first, straight from the database. Filters mirror the Windows System Logs tab so a
        // term or level set means the same thing in both GUIs.
        app.MapGet("/api/syslog", (string? level, string? search, bool? hideHeartbeats, int? limit) =>
        {
            List<string>? levelFilters = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                List<string> parsed = [.. level.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (parsed.Count > 0)
                    levelFilters = parsed;
            }

            try
            {
                IReadOnlyList<SystemLogEntry> entries = controller.Database.GetSystemLogs(
                    limit: Math.Clamp(limit ?? SystemLogDefaultLimit, 1, 2000),
                    levelFilters: levelFilters,
                    searchText: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                    excludeHeartbeats: hideHeartbeats == true);

                return Results.Json(new
                {
                    // When the database is unavailable the sink writes to a fallback file instead, so
                    // the tab can explain a suddenly empty list rather than looking broken.
                    dbHealthy = AppLogger.DbSink?.IsUsingDatabase ?? true,
                    entries = entries.Select(e => new
                    {
                        timestamp = e.Timestamp,
                        level = e.Level,
                        message = e.Message,
                        exception = e.Exception,
                        sourceContext = e.SourceContext,
                        isHeartbeat = e.IsHeartbeat,
                    }).ToList(),
                });
            }
            catch (Exception ex) when (ex is IOException or SqliteException)
            {
                Serilog.Log.Warning(ex, "System log query failed");
                return Results.Json(new { error = "System logs are temporarily unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Live streams first, then the short in-memory history of recently completed ones. The tails
        // are deliberately omitted here (they are up to 64 KB each) so a 2-second poll stays small —
        // the detail endpoint below supplies them when a row is opened.
        app.MapGet("/api/streams", () =>
        {
            StreamMonitor monitor = controller.StreamMonitor;
            return Results.Json(new
            {
                live = monitor.Snapshot().Select(s => ToStreamView(s, isLive: true)).ToList(),
                history = monitor.RecentHistory().Select(s => ToStreamView(s, isLive: false)).ToList(),
            });
        });

        app.MapGet("/api/streams/detail", (string? id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "A stream id is required." });

            // A stream can move from live to history (or age out) between the list poll and the
            // click, so search both before reporting it gone.
            StreamMonitor monitor = controller.StreamMonitor;
            StreamSnapshot? live = monitor.Snapshot().FirstOrDefault(s => string.Equals(s.RequestId, id, StringComparison.Ordinal));
            StreamSnapshot? snapshot = live ?? monitor.RecentHistory()
                .FirstOrDefault(s => string.Equals(s.RequestId, id, StringComparison.Ordinal));

            if (snapshot is null)
                return Results.Json(new { error = "That stream is no longer available." },
                    statusCode: StatusCodes.Status404NotFound);

            StreamView view = ToStreamView(snapshot, live is not null);
            view.RawUpstreamText = snapshot.RawUpstreamText;
            view.ForwardedText = snapshot.ForwardedText;
            return Results.Json(view);
        });
    }

    private static StreamView ToStreamView(StreamSnapshot snapshot, bool isLive) => new()
    {
        RequestId = snapshot.RequestId,
        Kind = snapshot.Kind.ToString(),
        State = snapshot.State.ToString(),
        Method = snapshot.Method,
        Path = snapshot.Path,
        Model = snapshot.Model,
        StartedUtc = snapshot.StartedUtc,
        LastUpstreamActivityUtc = snapshot.LastUpstreamActivityUtc,
        LastClientActivityUtc = snapshot.LastClientActivityUtc,
        UpstreamBytes = snapshot.UpstreamBytes,
        ClientBytes = snapshot.ClientBytes,
        KeepAliveFrames = snapshot.KeepAliveFrames,
        FrameCount = snapshot.FrameCount,
        IsLive = isLive,
        EndedUtc = snapshot.EndedUtc == DateTime.MinValue ? null : snapshot.EndedUtc,
    };
}