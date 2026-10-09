using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class OnnxEmbeddingBackend : IEmbeddingBackend
{
    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly int _maxSeqLen;
    private int _dimension;
    private string _modelName = "onnx";

    public OnnxEmbeddingBackend(string modelFolder, int maxSeqLen, int maxThreads)
    {
        _maxSeqLen = Math.Max(32, maxSeqLen);
        if (string.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder)) return;
        var modelPath = Path.Combine(modelFolder, "model.onnx");
        var vocabPath = Path.Combine(modelFolder, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath)) return;
        _modelName = Path.GetFileName(modelFolder);
        var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, maxThreads), GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _tokenizer = WordPieceTokenizer.LoadFromFile(vocabPath);
    }

    public string ModelName => _modelName;
    public int Dimension => _dimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = EmbedBatchAsync([text], ct);
        return results.ContinueWith(t => t.Result.Length > 0 ? t.Result[0] : [], ct);
    }

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (_session is null || _tokenizer is null) throw new InvalidOperationException("ONNX backend not initialized.");
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) { ct.ThrowIfCancellationRequested(); results[i] = EmbedSingle(texts[i]); }
        return Task.FromResult(results);
    }

    private float[] EmbedSingle(string text)
    {
        var tokenIds = _tokenizer!.Tokenize(text).Select(t => (long)t.Id).ToArray();
        if (tokenIds.Length > _maxSeqLen - 2) tokenIds = tokenIds.Take(_maxSeqLen - 2).ToArray();
        int seqLen = tokenIds.Length + 2;
        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];
        var tokenTypeIds = new long[seqLen];
        inputIds[0] = _tokenizer!.GetSpecialTokenId("[CLS]") ?? 101;
        for (int i = 0; i < tokenIds.Length; i++) { inputIds[i + 1] = tokenIds[i]; attentionMask[i + 1] = 1; }
        inputIds[seqLen - 1] = _tokenizer.GetSpecialTokenId("[SEP]") ?? 102;
        attentionMask[0] = 1;
        attentionMask[seqLen - 1] = 1;

        var inputMeta = _session!.InputMetadata;
        var inputs = new List<NamedOnnxValue>();
        foreach (var kv in inputMeta)
        {
            string name = kv.Key;
            long[] data = name switch { "input_ids" => inputIds, "attention_mask" => attentionMask, "token_type_ids" => tokenTypeIds, _ => attentionMask };
            if (kv.Value.ElementType == typeof(int))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data.Select(d => (int)d).ToArray(), [1, seqLen])));
            else
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, [1, seqLen])));
        }
        using var outputs = _session.Run(inputs);
        var outputTensor = outputs.First().AsTensor<float>();
        var shape = outputTensor.Dimensions;
        float[] embedding;
        if (shape.Length == 3)
        {
            int hidden = shape[2];
            embedding = new float[hidden];
            int count = 0;
            for (int s = 0; s < seqLen; s++)
            {
                if (attentionMask[s] == 0) continue;
                for (int h = 0; h < hidden; h++) embedding[h] += outputTensor[0, s, h];
                count++;
            }
            if (count > 0) for (int h = 0; h < hidden; h++) embedding[h] /= count;
        }
        else if (shape.Length == 2)
        {
            int hidden = shape[1];
            embedding = new float[hidden];
            for (int h = 0; h < hidden; h++) embedding[h] = outputTensor[0, h];
        }
        else { embedding = []; }

        float norm = 0f;
        for (int i = 0; i < embedding.Length; i++) norm += embedding[i] * embedding[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < embedding.Length; i++) embedding[i] /= norm;
        if (_dimension == 0) _dimension = embedding.Length;
        return embedding;
    }

    public void Dispose() => _session?.Dispose();
}
