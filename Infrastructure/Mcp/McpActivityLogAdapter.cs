using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Mcp;

/// <summary>
/// Maps <see cref="McpActivityEntry"/> records written by modules onto the host's MCP request
/// log so module activity shows up in the Logs tab (MCP sub-tab) next to the server's HTTP rows.
/// </summary>
internal sealed class McpActivityLogAdapter(StatisticsService statistics) : IMcpActivityLog
{
    private readonly StatisticsService _statistics = statistics;

    public void Write(McpActivityEntry entry)
    {
        try
        {
            RequestLog log = new()
            {
                Method = entry.Source,
                OllamaPath = entry.Target.Length == 0 ? entry.Operation : $"{entry.Operation} {entry.Target}",
                Status = entry.IsError
                    ? RequestStatus.Error
                    : entry.IsCancelled
                        ? RequestStatus.Cancelled
                        : RequestStatus.Success,
                StatusCode = entry.StatusCode,
                DurationMs = entry.DurationMs,
                ErrorMessage = entry.ErrorMessage,
                RequestBody = entry.RequestDetail,
                ResponseBody = entry.ResponseDetail,
                RequestBytes = entry.RequestDetail is null ? 0 : Encoding.UTF8.GetByteCount(entry.RequestDetail),
                ResponseBytes = entry.ResponseDetail is null ? 0 : Encoding.UTF8.GetByteCount(entry.ResponseDetail),
            };

            _statistics.AddLog(log);
        }
        catch (Exception ex)
        {
            // Logging must never break the operation being logged.
            Log.Warning(ex, "Failed to write MCP activity entry {Operation}", entry.Operation);
        }
    }
}
