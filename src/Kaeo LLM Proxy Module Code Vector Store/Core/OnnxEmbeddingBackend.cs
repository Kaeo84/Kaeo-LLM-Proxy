using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class OnnxEmbeddingBackend : IEmbeddingBackend
{
    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly int _maxSeqLen;
    private int _dimension;
    private readonly string _modelName = "onnx";

    public OnnxEmbeddingBackend(string modelFolder, int maxSeqLen, int maxThreads)
    {
        _maxSeqLen = Math.Max(32, maxSeqLen);
        // H8: fail fast with a descriptive error instead of silently constructing a backend
        // that throws "not initialized" on every embed call.
        if (string.IsNullOrWhiteSpace(modelFolder))
            throw new ArgumentException("ONNX model folder is not configured. Set it in the Code Vector Store settings.", nameof(modelFolder));
        if (!Directory.Exists(modelFolder))
            throw new DirectoryNotFoundException($"ONNX model folder not found: '{modelFolder}'.");
        var modelPath = Path.Combine(modelFolder, "model.onnx");
        var vocabPath = Path.Combine(modelFolder, "vocab.txt");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model file not found: '{modelPath}'.", modelPath);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"ONNX vocab file not found: '{vocabPath}'.", vocabPath);
        _modelName = Path.GetFileName(modelFolder);
        using var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, maxThreads), GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        var session = new InferenceSession(modelPath, options);
        try
        {
            _tokenizer = WordPieceTokenizer.LoadFromFile(vocabPath);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _session = session;
    }

    public string ModelName => _modelName;
    public int Dimension => Volatile.Read(ref _dimension);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct).ConfigureAwait(false);
        return results.Length > 0 ? results[0] : [];
    }

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (_session is null || _tokenizer is null) throw new InvalidOperationException("ONNX backend not initialized.");
        // M5: inference is CPU-bound and synchronous; run it on the thread pool instead of
        // blocking the caller (UI/MCP request thread) behind a fake Task.FromResult.
        return Task.Run(() =>
        {
            var results = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++) { ct.ThrowIfCancellationRequested(); results[i] = EmbedSingle(texts[i]); }
            return results;
        }, ct);
    }

    private float[] EmbedSingle(string text)
    {
        var tokenIds = _tokenizer!.Tokenize(text).Select(t => (long)t.Id).ToArray();
        if (tokenIds.Length > _maxSeqLen - 2) tokenIds = [.. tokenIds.Take(_maxSeqLen - 2)];
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
        float[] embedding;
        try
        {
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
        }
        finally
        {
            // M6: CreateFromTensor returns DisposableNamedOnnxValue instances that own
            // pinned/native buffers; dispose every input, even on failure.
            foreach (var input in inputs) (input as IDisposable)?.Dispose();
        }

        float norm = 0f;
        for (int i = 0; i < embedding.Length; i++) norm += embedding[i] * embedding[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < embedding.Length; i++) embedding[i] /= norm;
        // M12: concurrent batches publish the dimension atomically.
        Interlocked.CompareExchange(ref _dimension, embedding.Length, 0);
        return embedding;
    }

    public void Dispose() => _session?.Dispose();
}
