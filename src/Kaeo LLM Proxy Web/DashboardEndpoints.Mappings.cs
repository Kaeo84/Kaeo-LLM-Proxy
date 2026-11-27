using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapMappings(WebApplication app, ProxyHostController controller)
    {
        // The list is scoped by role: an Admin, a Model Admin, or a Models Read Only holder sees every
        // mapping, while an Own Models Read Only holder sees only the mappings granted to them. Users
        // with no model grant at all keep the full list, because the Usage filters and the user
        // editor's model picker are built from it.
        app.MapGet("/api/mappings", (HttpContext context) =>
        {
            IReadOnlyList<ModelMapping> mappings = controller.Settings.ModelMappings;
            return Results.Json(VisibleMappings(context, controller, mappings));
        });

        // Auto-discovery state for the Models tab: the master switch, the cadence, and what the last
        // scan did. Readable by anyone who can read the mapping list.
        app.MapGet("/api/mappings/discovery", () => Results.Json(new
        {
            enabled = controller.Settings.EnableModelAutoDiscovery,
            intervalSeconds = controller.Settings.ModelAutoDiscoveryIntervalSeconds,
            offlineMinutes = controller.Settings.ModelAutoDiscoveryOfflineMinutes,
            isRunning = controller.ModelDiscovery.IsRunning,
            lastScanUtc = controller.ModelDiscovery.LastScanUtc,
            lastResult = controller.ModelDiscovery.LastResult,
        }));

        // The OpenAPI contract for this surface, so a script author, a tool importer, or the Help
        // tab has one exact description to work from. Gated on ViewRegistration rather than being
        // open like the mapping list: it exists for the people and machines that use these routes.
        // Switching it off answers 404 exactly as if the route were never mapped, so a deployment
        // that does not want the contract published simply does not publish it.
        app.MapGet(RegistrationApiSpec.Path, () => controller.Settings.EnableRegistrationApiSpec
            ? Results.Text(RegistrationApiSpec.Build(), "application/json")
            : Results.NotFound());

        // "Discover now": one synchronous scan, so the operator sees the outcome immediately instead
        // of waiting for the next tick.
        app.MapPost("/api/mappings/discover", async () =>
        {
            try
            {
                return Results.Json(await controller.ModelDiscovery.RunOnceAsync());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Manual model discovery scan failed");
                return Results.Json(new { error = $"Discovery failed: {ex.Message}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/api/mappings", (ModelMapping mapping) =>
        {
            lock (AppSettings.ModelMappingsSyncRoot)
            {
                List<ModelMapping> list = controller.Settings.ModelMappings;
                int max = list.Count == 0 ? 0 : list.Max(m => m.Id);
                mapping.Id = max + 1;
                ModelMapping.TrackMaxId(mapping.Id);
                ClearAutoState(mapping);
                list.Add(mapping);
                controller.Database.SaveModelMappings(list);
            }
            return Results.Json(mapping);
        });

        app.MapPut("/api/mappings/{id:int}", (int id, ModelMapping mapping) =>
        {
            lock (AppSettings.ModelMappingsSyncRoot)
            {
                List<ModelMapping> list = controller.Settings.ModelMappings;
                int idx = list.FindIndex(m => m.Id == id);
                if (idx < 0)
                    return Results.NotFound(new { error = $"Mapping {id} not found." });
                mapping.Id = id;

                // An operator's edit outranks discovery's bookkeeping: turning auto-managed off, or
                // switching a disabled model back on by hand, clears the auto-disabled marker so the
                // service neither re-enables it later nor keeps claiming it disabled the model.
                ModelMapping stored = list[idx];
                if (!mapping.AutoManaged || (mapping.IsEnabled && !stored.IsEnabled))
                    ClearAutoState(mapping);

                list[idx] = mapping;
                controller.Database.SaveModelMappings(list);
            }
            return Results.Json(mapping);
        });

        app.MapDelete("/api/mappings/{id:int}", (int id) =>
        {
            lock (AppSettings.ModelMappingsSyncRoot)
            {
                List<ModelMapping> list = controller.Settings.ModelMappings;
                int removed = list.RemoveAll(m => m.Id == id);
                if (removed == 0)
                    return Results.NotFound(new { error = $"Mapping {id} not found." });
                controller.Database.SaveModelMappings(list);
            }
            return Results.Ok();
        });
    }

    /// <summary>
    /// The mappings the signed-in user may see. A user whose only model grant is
    /// <see cref="DashboardCapability.ViewOwnModels"/> is limited to their own grants; everyone else
    /// (Admin, Model Admin, Models Read Only, or no model grant at all) gets the full list.
    /// </summary>
    private static IReadOnlyList<ModelMapping> VisibleMappings(
        HttpContext context,
        ProxyHostController controller,
        IReadOnlyList<ModelMapping> mappings)
    {
        if (!DashboardAuth.Can(context, DashboardCapability.ViewOwnModels)
            || DashboardAuth.Can(context, DashboardCapability.ViewModels)
            || DashboardAuth.Can(context, DashboardCapability.ManageModels))
        {
            return mappings;
        }

        ProxyUser? user = CallerUser(context, controller);
        if (user is null || user.AllowedModels.Count == 0)
            return [];

        return [.. mappings.Where(m =>
            user.AllowedModels.Contains(m.ProxyName, StringComparer.OrdinalIgnoreCase)
            || user.AllowedModels.Contains(m.ModelName, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Drops discovery's bookkeeping from a mapping an operator just saved, so a manual edit is
    /// never mistaken for a discovery decision.
    /// </summary>
    private static void ClearAutoState(ModelMapping mapping)
    {
        mapping.AutoMissingSinceUtc = null;
        mapping.AutoDisabledUtc = null;
        mapping.AutoDisabledReason = null;
    }
}