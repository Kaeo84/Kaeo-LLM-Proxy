namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Risk classification for the built-in VS tools, used by the optional AutoApproveReadOnly
/// tier in Interactive mode. Only explicitly listed read-only tools auto-approve; everything
/// else (including unknown/MCP tools) keeps the normal permission gate.
/// </summary>
public static class ToolRisk
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "vs_file_search",
        "vs_get_file",
        "vs_find_symbol",
        "vs_get_projects",
        "vs_get_files_in_project",
        "vs_list_tests",
        "vs_get_diagnostics",
        "vs_get_errors",
        "vs_get_active_file",
        "vs_get_selection",
        "vs_get_output_logs",
        "vs_get_web_pages",
        "vs_detect_memories",
        "vs_list_instructions",
        "vs_read_instruction",
        "vs_read_notes",
        "vs_git_status",
        "vs_git_diff",
        "vs_git_log",
        "vs_checkpoint_list",
        "vs_checkpoint_diff",
        "vs_debug_get_state",
        "vs_debug_list_breakpoints",
    };

    /// <summary>True only for known built-in tools with no file/system mutation side effects.</summary>
    public static bool IsReadOnly(string toolName) => ReadOnlyTools.Contains(toolName);
}