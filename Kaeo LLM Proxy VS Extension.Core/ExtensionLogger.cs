using System;
using Serilog;
using Serilog.Events;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal static class ExtensionLogger
{
    private static ILogger? _logger;

    public static void Initialize(string? filePath, string level)
    {
        var levelSwitch = Serilog.Events.LogEventLevel.Information;
        Enum.TryParse<Serilog.Events.LogEventLevel>(level, true, out levelSwitch);

        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(levelSwitch)
            .Enrich.FromLogContext();

        if (!string.IsNullOrWhiteSpace(filePath))
            cfg = cfg.WriteTo.File(filePath, rollingInterval: RollingInterval.Day);

        _logger = cfg.CreateLogger();
    }

    public static void LogInformation(string message) => _logger?.Information(message);
    public static void LogError(Exception ex, string message) => _logger?.Error(ex, message);
}
