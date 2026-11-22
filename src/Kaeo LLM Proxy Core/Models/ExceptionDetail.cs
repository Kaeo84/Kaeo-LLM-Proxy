namespace Kaeo.LlmProxy.Core.Models;

/// <summary>Stores full exception information for a failed request.</summary>
internal sealed class ExceptionDetail
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string ExceptionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public List<string> InnerExceptions { get; set; } = [];

    // Request context copied from the associated RequestLog for quick reference.
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Builds an <see cref="ExceptionDetail"/> from an exception, capturing the full
    /// inner-exception chain and the relevant fields from <paramref name="log"/>.
    /// </summary>
    public static ExceptionDetail FromException(Exception ex, RequestLog log)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(log);

        var detail = new ExceptionDetail
        {
            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
            Message       = ex.Message,
            StackTrace    = ex.StackTrace,
            Method        = log.Method,
            Path          = log.OllamaPath,
            Model         = log.Model,
        };

        // Walk the inner-exception tree breadth-first so AggregateException fan-out is captured,
        // bounded in depth and guarded against self-referencing chains.
        const int maxDepth = 10;
        HashSet<Exception> visited = [ex];
        Queue<(Exception Ex, int Depth)> pending = new();
        pending.Enqueue((ex, 0));

        while (pending.Count > 0)
        {
            (Exception current, int depth) = pending.Dequeue();
            if (depth >= maxDepth)
                continue;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (!visited.Add(inner))
                        continue;
                    detail.InnerExceptions.Add($"{inner.GetType().Name}: {inner.Message}");
                    pending.Enqueue((inner, depth + 1));
                }
            }
            else if (current.InnerException is { } inner && visited.Add(inner))
            {
                detail.InnerExceptions.Add($"{inner.GetType().Name}: {inner.Message}");
                pending.Enqueue((inner, depth + 1));
            }
        }

        return detail;
    }
}