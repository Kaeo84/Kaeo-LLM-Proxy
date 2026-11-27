using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapCredentials(WebApplication app, ProxyHostController controller)
    {
        // Metadata only — secret material is never returned.
        app.MapGet("/api/credentials", () => Results.Json(controller.Settings.Credentials.Select(c => new
        {
            name = c.Name,
            description = c.Description,
            username = c.Username,
            hasSecretMaterial = c.HasSecretMaterial,
        }).ToList()));

        app.MapPost("/api/credentials", (CredentialWrite write) =>
        {
            if (string.IsNullOrWhiteSpace(write.Name))
                return Results.BadRequest(new { error = "Credential name is required." });

            lock (Sync)
            {
                List<StoredCredential> list = controller.Settings.Credentials;
                // Saving under an existing name edits that credential in place. Omitted secret
                // fields (null) keep their stored value, so an edit can change the description or
                // swap just the certificate without wiping the API key the caller never saw.
                StoredCredential? existing = list.FirstOrDefault(
                    c => string.Equals(c.Name, write.Name, StringComparison.OrdinalIgnoreCase));

                list.RemoveAll(c => string.Equals(c.Name, write.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(new StoredCredential
                {
                    Name = write.Name,
                    Description = write.Description,
                    Username = write.Username,
                    Secret = write.Secret ?? existing?.Secret ?? "",
                    PrivateKey = write.PrivateKey ?? existing?.PrivateKey,
                    Certificate = write.Certificate ?? existing?.Certificate,
                });
                controller.Database.SaveCredentials(list);
            }
            return Results.Ok();
        });

        app.MapDelete("/api/credentials/{name}", (string name) =>
        {
            lock (Sync)
            {
                List<StoredCredential> list = controller.Settings.Credentials;
                int removed = list.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                    return Results.NotFound(new { error = $"Credential '{name}' not found." });
                controller.Database.SaveCredentials(list);
            }
            return Results.Ok();
        });
    }
}