

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed record CodeChunk(int Index, int StartLine, int EndLine, string Text);
