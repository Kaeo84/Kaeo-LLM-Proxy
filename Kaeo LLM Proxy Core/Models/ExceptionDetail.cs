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
        var detail = new ExceptionDetail
        {
            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
            Message       = ex.Message,
            StackTrace    = ex.StackTrace,
            Method        = log.Method,
            Path          = log.OllamaPath,
            Model         = log.Model,
        };

        Exception? inner = ex.InnerException;
        while (inner is not null)
        {
            detail.InnerExceptions.Add($"{inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }

        return detail;
    }
}