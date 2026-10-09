using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal static class EmbeddingBackendFactory
{
    public static IEmbeddingBackend Create(CodeVectorSettings s, ISecretProvider secrets, HostInfo host)
    {
        return s.BackendType switch
        {
            BackendType.Onnx => new OnnxEmbeddingBackend(s.OnnxModelFolder, s.OnnxMaxSequenceLength, s.OnnxMaxThreads),
            BackendType.Remote => new RemoteEmbeddingBackend(s, secrets, host),
            _ => throw new InvalidOperationException($"Unsupported backend: {s.BackendType}"),
        };
    }
}
