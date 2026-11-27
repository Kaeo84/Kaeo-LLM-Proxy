using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapModules(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/modules", () => Results.Json(controller.ModuleHost.GetRegistryEntries().Select(e => new
        {
            id = e.Id,
            name = e.Name,
            moduleId = e.ModuleId,
            version = e.Version,
            assemblyPath = e.AssemblyPath,
            isEnabled = e.IsEnabled,
            registeredUtc = e.RegisteredUtc,
            lastError = e.LastError,
            signatureState = e.SignatureState,
            signerKeyId = e.SignerKeyId,
            isRunning = RunningState(controller, e.Id),
        }).ToList()));

        app.MapPost("/api/modules/{id:int}/toggle", async (int id, ModuleToggle body) =>
        {
            ModuleRegistryEntry? entry = controller.ModuleHost.GetRegistryEntries().FirstOrDefault(e => e.Id == id);
            if (entry is null)
                return Results.NotFound(new { error = $"Module {id} not found." });
            await controller.ModuleHost.SetEnabledAsync(entry, body.Enabled);
            return Results.Json(new { id, isEnabled = body.Enabled });
        });

        // Upload a .kaeomod package: verified (hashes + signature + policy) and extracted by the
        // same ImportAsync seam as browse-to-import, then loaded. The temp copy is always removed.
        app.MapPost("/api/modules/upload", async (HttpContext context) =>
        {
            // Lift Kestrel's default 30 MB body cap for this endpoint; the package size cap is
            // enforced below (and by FormOptions.MultipartBodyLengthLimit during form reading).
            IHttpMaxRequestBodySizeFeature? sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is not null && !sizeFeature.IsReadOnly)
                sizeFeature.MaxRequestBodySize = null;

            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Upload must be multipart/form-data with a 'package' file." });

            IFormFile? file;
            try
            {
                IFormCollection form = await context.Request.ReadFormAsync(
                    new FormOptions { MultipartBodyLengthLimit = ModulePackageReader.MaxPackageBytes });
                file = form.Files.GetFile("package") ?? (form.Files.Count == 1 ? form.Files[0] : null);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = $"The upload could not be read: {ex.Message}" });
            }

            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No package file was uploaded." });
            if (file.Length > ModulePackageReader.MaxPackageBytes)
                return Results.Json(new { error = $"The package exceeds the {ModulePackageReader.MaxPackageBytes / (1024 * 1024)} MB upload cap." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            string tempPath = Path.Combine(Path.GetTempPath(), $"kaeo-module-upload-{Guid.NewGuid():N}.kaeomod");
            try
            {
                await using (FileStream stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                ModuleRegistryEntry entry = await controller.ModuleHost.ImportAsync(tempPath);
                return Results.Json(new
                {
                    id = entry.Id,
                    name = entry.Name,
                    moduleId = entry.ModuleId,
                    version = entry.Version,
                    signatureState = entry.SignatureState,
                    signerKeyId = entry.SignerKeyId,
                });
            }
            catch (Exception ex) when (ex is ModulePackageException or InvalidOperationException or TimeoutException or FileNotFoundException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                // Another instance may hold the database; report retryable instead of crashing.
                return Results.Json(new { error = $"Could not register the uploaded module: {ex.Message}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Temp cleanup is best-effort; the module loads from the extracted copy.
                }
            }
        });

        app.MapGet("/api/modules/{id:int}/config", (int id) =>
        {
            LoadedModule? loaded = controller.ModuleHost.LoadedModules.FirstOrDefault(l => l.Entry.Id == id);
            if (loaded is null)
                return Results.NotFound(new { error = $"Module {id} is not loaded." });
            if (loaded.Module is not IModuleSettingsJson json)
                return Results.Json(new { error = "This module does not expose JSON settings." }, statusCode: StatusCodes.Status501NotImplemented);
            return Results.Content(json.GetSettingsJson(), "application/json");
        });

        app.MapPut("/api/modules/{id:int}/config", async (int id, HttpContext context) =>
        {
            LoadedModule? loaded = controller.ModuleHost.LoadedModules.FirstOrDefault(l => l.Entry.Id == id);
            if (loaded is null)
                return Results.NotFound(new { error = $"Module {id} is not loaded." });
            if (loaded.Module is not IModuleSettingsJson json)
                return Results.Json(new { error = "This module does not expose JSON settings." }, statusCode: StatusCodes.Status501NotImplemented);

            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync();
            try
            {
                json.ApplySettingsJson(body);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                // Another instance may hold the database; report retryable instead of crashing.
                return Results.Json(new { error = $"Could not persist module settings: {ex.Message}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok();
        });
    }
}