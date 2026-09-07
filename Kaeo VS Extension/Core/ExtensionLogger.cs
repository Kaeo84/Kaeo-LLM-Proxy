using System;
using System.Diagnostics;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal static class ExtensionLogger
{
    public static void Initialize(string? filePath, string level)
    {
        // No-op: Serilog removed, logging can be added later via Debug.WriteLine if needed
        Debug.WriteLine($"[ExtensionLogger] Initialized with level: {level}, file: {filePath ?? "none"}");
    }

    public static void LogInformation(string message)
    {
        Debug.WriteLine($"[ExtensionLogger] INFO: {message}");
    }

    public static void LogError(Exception ex, string message)
    {
        Debug.WriteLine($"[ExtensionLogger] ERROR: {message} - {ex.Message}");
    }
}
