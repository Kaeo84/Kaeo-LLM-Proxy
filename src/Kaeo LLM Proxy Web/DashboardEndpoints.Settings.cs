using Kaeo.LlmProxy.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapSettings(WebApplication app, AppSettings settings)
    {
        app.MapGet("/api/settings", () => Results.Json(BuildSettingsView(settings)));

        app.MapPut("/api/settings", (SettingsUpdate update) =>
        {
            string? error = ValidateSettings(update);
            if (error is not null)
                return Results.BadRequest(new { error });

            lock (Sync)
            {
                ApplySettings(settings, update);
                settings.Save();
            }
            return Results.Json(BuildSettingsView(settings));
        });
    }

    private static object BuildSettingsView(AppSettings s)
    {
        WebDashboardSettings wd = s.WebDashboard;
        return new
        {
            listenPort = s.ListenPort,
            listenAddress = s.ListenAddress,
            autoStartProxy = s.AutoStartProxy,
            maxConcurrentRequests = s.MaxConcurrentRequests,
            maxRequestBodyBytes = s.MaxRequestBodyBytes,
            maxLogEntries = s.MaxLogEntries,
            enableCors = s.EnableCors,
            collectRequestDetails = s.CollectRequestDetails,
            collectResponseDetails = s.CollectResponseDetails,
            debugMode = s.DebugMode,
            hideHeartbeatLogs = s.HideHeartbeatLogs,
            enableSseKeepAlive = s.EnableSseKeepAlive,
            sseKeepAliveIntervalSeconds = s.SseKeepAliveIntervalSeconds,
            heartbeatIntervalSeconds = s.HeartbeatIntervalSeconds,
            enablePerformanceSampling = s.EnablePerformanceSampling,
            enableApiExplorer = s.EnableApiExplorer,
            logging = new
            {
                logRetentionHours = s.Logging.LogRetentionHours,
                maxLogEntriesWithBodies = s.Logging.MaxLogEntriesWithBodies,
            },
            webDashboard = new
            {
                enabled = wd.Enabled,
                port = wd.Port,
                listenAddress = wd.ListenAddress,
                requireApiKey = wd.RequireApiKey,
                scheme = wd.Scheme,
                certificatePath = wd.CertificatePath,
                hasApiKey = !string.IsNullOrEmpty(wd.ApiKey),
                hasCertificatePassword = !string.IsNullOrEmpty(wd.CertificatePassword),
            },
            proxySecurity = new
            {
                requireApiKey = s.ProxySecurity.RequireApiKey,
            },
        };
    }

    private static string? ValidateSettings(SettingsUpdate u)
    {
        if (u.ListenPort is < 1 or > 65535)
            return "ListenPort must be between 1 and 65535.";
        if (u.MaxConcurrentRequests is < 1)
            return "MaxConcurrentRequests must be at least 1.";
        if (u.MaxLogEntries is < 1)
            return "MaxLogEntries must be at least 1.";
        if (u.MaxRequestBodyBytes is < 1)
            return "MaxRequestBodyBytes must be at least 1.";
        if (u.SseKeepAliveIntervalSeconds is < 1)
            return "SseKeepAliveIntervalSeconds must be at least 1.";
        if (u.HeartbeatIntervalSeconds is < 1)
            return "HeartbeatIntervalSeconds must be at least 1.";
        if (u.WebDashboard is { } wd)
        {
            if (wd.Port is < 0 or > 65535)
                return "WebDashboard.Port must be between 0 and 65535 (0 = proxy port + 1).";
            if (wd.Scheme is not null && !wd.Scheme.Equals("Http", StringComparison.OrdinalIgnoreCase)
                && !wd.Scheme.Equals("Https", StringComparison.OrdinalIgnoreCase))
                return "WebDashboard.Scheme must be 'Http' or 'Https'.";
        }
        return null;
    }

    private static void ApplySettings(AppSettings s, SettingsUpdate u)
    {
        if (u.ListenPort is { } lp) s.ListenPort = lp;
        if (u.ListenAddress is { } la) s.ListenAddress = la;
        if (u.AutoStartProxy is { } asp) s.AutoStartProxy = asp;
        if (u.MaxConcurrentRequests is { } mcr) s.MaxConcurrentRequests = mcr;
        if (u.MaxRequestBodyBytes is { } mrb) s.MaxRequestBodyBytes = mrb;
        if (u.MaxLogEntries is { } mle) s.MaxLogEntries = mle;
        if (u.EnableCors is { } cors) s.EnableCors = cors;
        if (u.CollectRequestDetails is { } crd) s.CollectRequestDetails = crd;
        if (u.CollectResponseDetails is { } csd) s.CollectResponseDetails = csd;
        if (u.DebugMode is { } dm) s.DebugMode = dm;
        if (u.HideHeartbeatLogs is { } hhl) s.HideHeartbeatLogs = hhl;
        if (u.EnableSseKeepAlive is { } sse) s.EnableSseKeepAlive = sse;
        if (u.SseKeepAliveIntervalSeconds is { } ssei) s.SseKeepAliveIntervalSeconds = ssei;
        if (u.HeartbeatIntervalSeconds is { } hbi) s.HeartbeatIntervalSeconds = hbi;
        if (u.EnablePerformanceSampling is { } eps) s.EnablePerformanceSampling = eps;
        if (u.EnableApiExplorer is { } eae) s.EnableApiExplorer = eae;

        if (u.WebDashboard is { } wd)
        {
            WebDashboardSettings target = s.WebDashboard;
            if (wd.Enabled is { } en) target.Enabled = en;
            if (wd.Port is { } p) target.Port = p;
            if (wd.ListenAddress is { } wla) target.ListenAddress = wla;
            if (wd.RequireApiKey is { } rak) target.RequireApiKey = rak;
            if (wd.Scheme is { } sch) target.Scheme = sch;
            if (wd.CertificatePath is { } cp) target.CertificatePath = cp;
            if (wd.ApiKey is { } ak) target.ApiKey = ak;
            if (wd.CertificatePassword is { } cpw) target.CertificatePassword = cpw;
        }

        if (u.ProxySecurity is { } ps && ps.RequireApiKey is { } prak)
            s.ProxySecurity.RequireApiKey = prak;
    }
}