using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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