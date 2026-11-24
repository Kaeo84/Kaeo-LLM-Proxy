using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapMappings(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/mappings", () => Results.Json(controller.Settings.ModelMappings));

        app.MapPost("/api/mappings", (ModelMapping mapping) =>
        {
            lock (Sync)
            {
                List<ModelMapping> list = controller.Settings.ModelMappings;
                int max = list.Count == 0 ? 0 : list.Max(m => m.Id);
                mapping.Id = max + 1;
                ModelMapping.TrackMaxId(mapping.Id);
                list.Add(mapping);
                controller.Database.SaveModelMappings(list);
            }
            return Results.Json(mapping);
        });

        app.MapPut("/api/mappings/{id:int}", (int id, ModelMapping mapping) =>
        {
            lock (Sync)
            {
                List<ModelMapping> list = controller.Settings.ModelMappings;
                int idx = list.FindIndex(m => m.Id == id);
                if (idx < 0)
                    return Results.NotFound(new { error = $"Mapping {id} not found." });
                mapping.Id = id;
                list[idx] = mapping;
                controller.Database.SaveModelMappings(list);
            }
            return Results.Json(mapping);
        });

        app.MapDelete("/api/mappings/{id:int}", (int id) =>
        {
            lock (Sync)
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
}