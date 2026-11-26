using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
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
/// on Windows/Linux/macOS. HTTP by default (TLS terminated upstream); optional HTTPS via a
/// certificate taken from the credential store. Created through
/// <see cref="WebDashboardService"/> so both GUIs and the dashboard's own API can report and control
/// the listener; the host attaches itself to that service on start and detaches on stop, so state
/// stays correct however it was constructed (entry points, tests, or an explicit Start click).
/// </summary>
internal sealed class WebDashboardHost : IWebDashboardHost
{
    private readonly AppSettings _settings;
    private readonly ProxyHostController _controller;
    private readonly StatisticsService _webStats;
    private WebApplication? _app;
    private bool _disposed;

    public WebDashboardHost(AppSettings settings, ProxyHostController controller, StatisticsService? webStats = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _webStats = webStats ?? controller.WebStats;
    }

    public bool IsRunning => _app is not null;

    /// <summary>The base URL the dashboard is listening on, once started.</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// Starts the listener. <paramref name="force"/> starts it even when the dashboard is disabled
    /// in settings, which is what an explicit Start/Restart click means; without it a disabled
    /// dashboard declines to start so the boot path never opens a port the user turned off.
    /// </summary>
    public async Task StartAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return;

        WebDashboardSettings wd = _settings.WebDashboard;
        if (!wd.Enabled && !force)
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
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = ".KaeoDashboard.Session";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.Cookie.IsEssential = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;
                    options.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    };
                });
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("dashboard-login", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            });
            builder.WebHost.ConfigureKestrel(options => ConfigureListen(options, wd, port, https));

            WebApplication app = builder.Build();
            app.UseRateLimiter();
            app.UseAuthentication();

            // Sits outside the auth gate so rejected (401/403) attempts are logged too: an
            // unauthorized caller is exactly the traffic worth seeing.
            app.Use(async (context, next) =>
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    await next();
                }
                finally
                {
                    RecordWebTraffic(context, started);
                }
            });

            app.Use(async (context, next) =>
            {
                HttpRequest request = context.Request;
                if (DashboardAuth.IsAuthenticationEndpoint(request) || DashboardAuth.IsPublicAsset(request.Path))
                {
                    await next();
                    return;
                }

                if (request.Path.StartsWithSegments("/api"))
                {
                    if (!DashboardAuth.TryAuthorize(context, _settings, _controller.Database, out string? error))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new { error });
                        return;
                    }

                    if (Equals(context.Items[DashboardAuth.RoleItemKey], DashboardAuth.ReadOnlyRole)
                        && !DashboardAuth.IsReadOnlyRequestAllowed(request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "Read-only dashboard accounts can access logs only." });
                        return;
                    }

                    await next();
                    return;
                }

                // Everything else (index.html, app.js, ...) requires a signed-in session unless
                // the login requirement is turned off in settings.
                if (_settings.WebDashboard.RequireLogin && context.User.Identity?.IsAuthenticated != true)
                {
                    if (HttpMethods.IsGet(request.Method))
                    {
                        context.Response.Redirect("/login.html");
                        return;
                    }
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });
            app.UseDefaultFiles();
            app.UseStaticFiles();

            DashboardEndpoints.MapAll(app, _settings, _controller);

            await app.StartAsync(cancellationToken);
            _app = app;

            string host = DashboardAuth.IsLoopbackAddress(wd.ListenAddress) ? "localhost" : wd.ListenAddress;
            Url = $"{(https ? "https" : "http")}://{host}:{port}";
            _controller.WebDashboard.Attach(this);
            Log.Information("Web dashboard listening on {Url} ({LoginMode})", Url,
                wd.RequireLogin ? "login required" : "login disabled — full access for anyone who can reach it");
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
        _controller.WebDashboard.Detach(this);
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

        // Hand this host's buffered traffic to the database now: the persistence loop may not get
        // another pass during shutdown, and the dashboard's last entries are the ones that explain
        // who stopped it.
        try
        {
            await _webStats.FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error flushing web traffic log entries during dashboard shutdown");
        }
    }

    private void ConfigureListen(KestrelServerOptions options, WebDashboardSettings wd, int port, bool https)
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

    /// <summary>
    /// Resolves the HTTPS certificate: a named credential from the credential store (preferred,
    /// because the material is encrypted at rest) or the legacy PFX path/password pair.
    /// </summary>
    private X509Certificate2 LoadCertificate(WebDashboardSettings wd)
    {
        if (!string.IsNullOrWhiteSpace(wd.CertificateCredentialName))
            return LoadCertificateFromCredential(wd.CertificateCredentialName.Trim());

        if (string.IsNullOrWhiteSpace(wd.CertificatePath))
        {
            throw new InvalidOperationException(
                "WebDashboard.Scheme is Https but no certificate is configured. Select a certificate credential in Settings.");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(wd.CertificatePath, wd.CertificatePassword);
    }

    /// <summary>
    /// Builds the certificate from a stored credential. The credential's <c>Certificate</c> field
    /// holds either a base64 PFX/PKCS#12 or a PEM certificate (paired with the credential's
    /// <c>PrivateKey</c> for the key); the credential's <c>Secret</c> is the PFX password, if any.
    /// </summary>
    private X509Certificate2 LoadCertificateFromCredential(string name)
    {
        StoredCredential credential;
        lock (_settings.CredentialsSyncRoot)
        {
            credential = _settings.Credentials.FirstOrDefault(
                    c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Web dashboard HTTPS certificate credential '{name}' was not found. Select an existing credential in Settings.");
        }

        string material = credential.Certificate ?? string.Empty;
        if (string.IsNullOrWhiteSpace(material))
        {
            throw new InvalidOperationException(
                $"Credential '{name}' has no certificate material. Put a PFX (base64) or PEM certificate in the credential's Certificate field.");
        }

        string trimmed = material.Trim();
        if (trimmed.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            return LoadPemCertificate(trimmed, credential.PrivateKey, name);

        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(trimmed);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Credential '{name}' does not hold a valid certificate: expected base64 PFX/PKCS#12 data or a PEM certificate.", ex);
        }

        try
        {
            // An empty Secret means the PFX has no password; pass null rather than "" so the
            // loader does not try to match an empty password against a passwordless file.
            string? password = string.IsNullOrEmpty(credential.Secret) ? null : credential.Secret;
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Credential '{name}' could not be loaded as a PFX certificate. Check that its Secret holds the PFX password, if the file has one.", ex);
        }
    }

    private static X509Certificate2 LoadPemCertificate(string certificatePem, string? privateKeyPem, string name)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException(
                $"Credential '{name}' holds a PEM certificate but no private key. Add the key to the credential's PrivateKey field — HTTPS needs the private key.");
        }

        try
        {
            using X509Certificate2 ephemeral = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem.Trim());
            // Round-trip through PKCS#12: an ephemeral PEM key cannot be used by SslStream on
            // Windows, and this produces a certificate SChannel accepts.
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.DefaultKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Credential '{name}' could not be loaded as a PEM certificate/private key pair.", ex);
        }
    }

    /// <summary>
    /// Records one dashboard request as web traffic. Never throws: traffic logging must not be able
    /// to break the request it describes.
    /// </summary>
    private void RecordWebTraffic(HttpContext context, long startedTimestamp)
    {
        try
        {
            if (_disposed || ShouldSkipWebTraffic(context))
                return;

            int statusCode = context.Response.StatusCode;
            string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";

            RequestLog entry = new()
            {
                RequestId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow,
                Method = context.Request.Method,
                OllamaPath = path,
                UpstreamPath = string.Empty,
                Model = string.Empty,
                Streaming = false,
                Status = RequestLog.DeriveStatus(statusCode),
                StatusCode = statusCode,
                DurationMs = Math.Round(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds, 1),
                RequestBytes = context.Request.ContentLength ?? 0,
                ResponseBytes = context.Response.ContentLength ?? -1,
                ClientAddress = NormalizeClientAddress(context.Connection.RemoteIpAddress),
                UserAgent = context.Request.Headers.UserAgent.Count > 0
                    ? context.Request.Headers.UserAgent.ToString()
                    : null,
                UserId = DashboardAuth.ResolveUserId(context),
            };

            if (entry.Status != RequestStatus.Success)
                entry.ErrorMessage = $"HTTP {statusCode}";

            _webStats.AddLog(entry);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to record web dashboard traffic for {Path}", context.Request.Path);
        }
    }

    /// <summary>
    /// Drops the noise from the web traffic table: page chrome (one page load would otherwise
    /// produce a row per script and stylesheet) and the dashboard's own high-frequency polling,
    /// which a single idle browser tab emits every few seconds forever. Failures are always kept,
    /// so a broken poll still shows up.
    /// </summary>
    private static bool ShouldSkipWebTraffic(HttpContext context)
    {
        if (context.Response.StatusCode >= 400)
            return false;

        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";

        if (path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
            return false;

        // Read-only polling. Mutations (sign-in, settings, start/stop) are always recorded.
        return path.Equals("/api/status", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/stats", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/logs/detail", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/syslog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/streams", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/streams/detail", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/usage", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeClientAddress(IPAddress? address)
        => address is null ? null : address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
}