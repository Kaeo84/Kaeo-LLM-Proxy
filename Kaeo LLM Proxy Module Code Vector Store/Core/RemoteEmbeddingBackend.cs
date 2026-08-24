using Kaeo.LlmProxy.Core.Modules;
using Serilog;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class RemoteEmbeddingBackend : IEmbeddingBackend
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly string _model;
    private int _dimension;
    private string _modelName;

    public RemoteEmbeddingBackend(CodeVectorSettings settings, ISecretProvider secrets, HostInfo host)
    {
        string url = string.IsNullOrWhiteSpace(settings.RemoteUrl)
            ? $"http://{host.DisplayHost}:{host.ListenPort}/v1/embeddings"
            : settings.RemoteUrl.Trim();

        // Users typically enter just the host (e.g. http://192.168.1.1:8081).
        // Append the embeddings endpoint if the path is not already specified.
        if (!url.Contains("/v1/", StringComparison.OrdinalIgnoreCase)
            && !url.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            url = url.TrimEnd('/') + "/v1/embeddings";

        _url = url;
        _model = string.IsNullOrWhiteSpace(settings.RemoteModel) ? "default" : settings.RemoteModel;
        _modelName = _model;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.RemoteTimeoutSeconds) };

        if (!string.IsNullOrWhiteSpace(settings.RemoteCredentialName))
        {
            var secret = secrets.ResolveSecret(settings.RemoteCredentialName);
            if (!string.IsNullOrWhiteSpace(secret))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    public string ModelName => _modelName;
    public int Dimension => _dimension;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results.Length > 0 ? results[0] : [];
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var requestBody = new { model = _model, input = texts.ToArray() };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        Log.Debug("Embedding request: POST {Url} model={Model} count={Count}", _url, _model, texts.Count);
        using var response = await _httpClient.PostAsync(_url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Embedding request failed: {(int)response.StatusCode} {response.ReasonPhrase}\nURL: {_url}\nModel: {_model}\nResponse: {errBody}");
        }
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        var data = doc.RootElement.GetProperty("data");
        var results = new float[data.GetArrayLength()][];
        int idx = 0;
        foreach (var item in data.EnumerateArray())
        {
            var embedding = item.GetProperty("embedding");
            var vec = new float[embedding.GetArrayLength()];
            int i = 0;
            foreach (var val in embedding.EnumerateArray()) vec[i++] = val.GetSingle();
            results[idx++] = vec;
        }
        if (_dimension == 0 && results.Length > 0) _dimension = results[0].Length;
        return results;
    }

    public void Dispose() => _httpClient.Dispose();
}
