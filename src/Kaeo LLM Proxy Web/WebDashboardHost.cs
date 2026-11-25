using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Builds and runs the ASP.NET Core (Kestrel) JSON dashboard on its own port. Portable: identical
/// on Windows/Linux/macOS. HTTP by default (TLS terminated upstream); optional HTTPS via a supplied
/// PFX certificate. Started and stopped by each host entry point (headless + tray) — the controller
/// in Services stays UI-agnostic and does not reference this assembly.
/// </summary>
internal sealed class WebDashboardHost : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ProxyHostController _controller;
    private WebApplication? _app;
    private bool _disposed;

    public WebDashboardHost(AppSettings settings, ProxyHostController controller)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public bool IsRunning => _app is not null;

    /// <summary>The base URL the dashboard is listening on, once started.</summary>
    public string? Url { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return;

        WebDashboardSettings wd = _settings.WebDashboard;
        if (!wd.Enabled)
        {
            Log.Information("Web dashboard disabled by configuration");
            return;
        }

        int port = wd.GetEffectivePort(_settings.ListenPort);
        bool https = string.Equals(wd.Scheme, "Https", StringComparison.OrdinalIgnoreCase);

        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
            builder.WebHost.ConfigureKestrel(options => ConfigureListen(options, wd, port, https));

            WebApplication app = builder.Build();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    if (!DashboardAuth.TryAuthorize(context, wd, out string? error))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new { error });
                        return;
                    }

                    if (Equals(context.Items[DashboardAuth.RoleItemKey], DashboardAuth.ReadOnlyRole)
                        && !DashboardAuth.IsReadOnlyRequestAllowed(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "Read-only dashboard credentials can access logs only." });
                        return;
                    }
                }
                await next();
            });

            DashboardEndpoints.MapAll(app, _settings, _controller);

            await app.StartAsync(cancellationToken);
            _app = app;

            string host = DashboardAuth.IsLoopbackAddress(wd.ListenAddress) ? "localhost" : wd.ListenAddress;
            Url = $"{(https ? "https" : "http")}://{host}:{port}";
            Log.Information("Web dashboard listening on {Url} (auth={AuthMode})", Url, DashboardAuth.AuthMode(wd));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start web dashboard on port {Port}", port);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        WebApplication app = _app;
        _app = null;
        Url = null;
        try
        {
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping web dashboard");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync();
    }

    private static void ConfigureListen(KestrelServerOptions options, WebDashboardSettings wd, int port, bool https)
    {
        Action<ListenOptions> configure = https ? lo => lo.UseHttps(LoadCertificate(wd)) : _ => { };
        string address = wd.ListenAddress ?? "localhost";

        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            options.ListenLocalhost(port, configure);
        else if (address is "*" or "+" or "0.0.0.0" || address.Equals("any", StringComparison.OrdinalIgnoreCase))
            options.ListenAnyIP(port, configure);
        else if (IPAddress.TryParse(address, out IPAddress? ip))
            options.Listen(ip, port, configure);
        else
            options.ListenLocalhost(port, configure);
    }

    private static X509Certificate2 LoadCertificate(WebDashboardSettings wd)
    {
        if (string.IsNullOrWhiteSpace(wd.CertificatePath))
            throw new InvalidOperationException("WebDashboard.Scheme is Https but CertificatePath is not set.");
        return X509CertificateLoader.LoadPkcs12FromFile(wd.CertificatePath, wd.CertificatePassword);
    }
}