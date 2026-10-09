using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests.E2E;

/// <summary>
/// Self-diagnostic tests that probe the user's actual configured upstream models, pick one
/// whose heartbeat is currently succeeding, and send a simple &quot;Test&quot; chat request
/// through it — both non-streaming and streaming.  These run against the real upstream (not
/// a stub), so they validate end‑to‑end connectivity without requiring the user to manually
/// open the dashboard.  When no healthy model is found all tests skip rather than fail.
/// </summary>
[Collection("AppLogger")]
public sealed class LiveModelSmokeTests
{
    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Picks the first model mapping whose upstream /v1/models probe last succeeded.
    /// Returns null when no healthy mapping exists.
    /// </summary>
    private static ModelMapping? FindHealthyModel()
    {
        AppSettings settings = AppSettings.Load();
        using AppDatabase db = new(settings.Logging);
        settings.ApplyRuntimeSettings(db.LoadRuntimeSettings());

        IReadOnlyList<ModelMapping> mappings = db.LoadModelMappings();
        foreach (ModelMapping mapping in mappings
                     .Where(m => m.IsEnabled)
                     .OrderBy(m => m.ProxyName, StringComparer.Ordinal))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{mapping.UpstreamUrl.TrimEnd('/')}/v1/models");
                ApplyApiKey(request, mapping);
                using HttpResponseMessage resp = _client.Send(request);
                if (resp.IsSuccessStatusCode)
                    return mapping;
            }
            catch
            {
                // This upstream is unreachable — try the next one.
            }
        }

        return null;
    }

    private static void ApplyApiKey(HttpRequestMessage request, ModelMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.CredentialName))
            return;

        AppSettings settings = AppSettings.Load();
        using AppDatabase db = new(settings.Logging);
        IReadOnlyList<StoredCredential> credentials = db.LoadCredentials();
        StoredCredential? cred = credentials
            .FirstOrDefault(c => string.Equals(c.Name, mapping.CredentialName,
                StringComparison.OrdinalIgnoreCase));

        if (cred is not null && !string.IsNullOrWhiteSpace(cred.Secret))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cred.Secret}");
    }

    [Fact]
    public async Task NonStreamingChat_SendsTest_ReceivesContent()
    {
        ModelMapping? model = FindHealthyModel();
        if (model is null)
            return; // skip — no healthy upstream to test against

        var payload = new
        {
            model = model.ModelName,
            messages = new[]
            {
                new { role = "user", content = "Reply with exactly the word: Test" },
            },
            stream = false,
            max_tokens = 10,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{model.UpstreamUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        ApplyApiKey(request, model);

        using HttpResponseMessage resp = await _client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        string? reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Assert.NotNull(reply);
        Assert.NotEmpty(reply);
    }

    [Fact]
    public async Task StreamingChat_SendsTest_ReceivesToken()
    {
        ModelMapping? model = FindHealthyModel();
        if (model is null)
            return; // skip

        var payload = new
        {
            model = model.ModelName,
            messages = new[]
            {
                new { role = "user", content = "Reply with exactly the word: Test" },
            },
            stream = true,
            max_tokens = 10,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{model.UpstreamUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        ApplyApiKey(request, model);

        using HttpResponseMessage resp = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);

        resp.EnsureSuccessStatusCode();

        using Stream stream = await resp.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream, Encoding.UTF8);

        string? contentToken = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ") || line == "data: [DONE]")
                continue;

            string json = line[6..];
            using JsonDocument chunk = JsonDocument.Parse(json);
            if (chunk.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices[0].TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("content", out JsonElement c)
                && c.GetString() is { Length: > 0 } text)
            {
                contentToken = text;
            }
        }

        Assert.NotNull(contentToken);
        Assert.NotEmpty(contentToken);
    }
}