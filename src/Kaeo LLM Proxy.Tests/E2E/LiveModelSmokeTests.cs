using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
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
    /// Snapshot of the app's configured state, loaded once for the whole test class: the enabled
    /// model mappings plus every stored credential secret decrypted to plaintext. The previous
    /// version called <see cref="AppSettings.Load"/> and opened a new <see cref="AppDatabase"/>
    /// (SQLite) on every HTTP request, which wasted disk I/O and risked file-lock contention with a
    /// concurrently running proxy instance.
    /// </summary>
    private sealed record Fixture(
        IReadOnlyList<ModelMapping> Mappings,
        IReadOnlyDictionary<string, string> DecryptedSecrets);

    private static readonly Lazy<Fixture> _fixture = new(LoadFixture);

    private static Fixture LoadFixture()
    {
        AppSettings settings = AppSettings.Load();
        using AppDatabase db = new(settings.Logging);
        settings.ApplyRuntimeSettings(db.LoadRuntimeSettings());

        // Resolve the passphrase material once, then decrypt every credential secret so the smoke
        // tests authenticate with the real key rather than sending its stored (possibly encrypted)
        // form. A secret that is not encrypted is used as-is; one that cannot be decrypted (e.g. no
        // passphrase available in this context) falls back to its stored value so the mapping
        // simply fails to authenticate instead of throwing.
        string passphrase = SecretProtector.ResolvePassphraseMaterial(settings.SecurityPassphrase);
        Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase);
        foreach (StoredCredential cred in db.LoadCredentials())
        {
            if (string.IsNullOrWhiteSpace(cred.Secret))
                continue;

            secrets[cred.Name] = SecretProtector.IsEncrypted(cred.Secret)
                && SecretProtector.TryDecrypt(cred.Secret, passphrase, out string? plain)
                && plain is not null
                    ? plain
                    : cred.Secret;
        }

        return new Fixture(db.LoadModelMappings(), secrets);
    }

    /// <summary>
    /// Picks the first model mapping whose upstream /v1/models probe succeeds.
    /// Returns null when no healthy mapping exists.
    /// </summary>
    private static ModelMapping? FindHealthyModel()
    {
        Fixture fixture = _fixture.Value;
        foreach (ModelMapping mapping in fixture.Mappings
                     .Where(m => m.IsEnabled)
                     .OrderBy(m => m.ProxyName, StringComparer.Ordinal))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{mapping.UpstreamUrl.TrimEnd('/')}/v1/models");
                ApplyApiKey(request, mapping, fixture);
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

    private static void ApplyApiKey(HttpRequestMessage request, ModelMapping mapping, Fixture fixture)
    {
        if (string.IsNullOrWhiteSpace(mapping.CredentialName))
            return;

        if (fixture.DecryptedSecrets.TryGetValue(mapping.CredentialName!, out string? secret)
            && !string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
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
        ApplyApiKey(request, model, _fixture.Value);

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
        ApplyApiKey(request, model, _fixture.Value);

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