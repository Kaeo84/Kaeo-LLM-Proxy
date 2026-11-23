using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the read-only risk tier: only explicitly listed read-only tools auto-approve;
/// every mutating tool (including the AST editors) and every unknown/MCP tool stays gated.
/// </summary>
public class ToolRiskTests
{
    [Theory]
    [InlineData("vs_get_file")]
    [InlineData("vs_file_search")]
    [InlineData("vs_find_symbol")]
    [InlineData("vs_git_status")]
    [InlineData("vs_git_diff")]
    [InlineData("vs_checkpoint_list")]
    [InlineData("vs_checkpoint_diff")]
    [InlineData("vs_read_notes")]
    [InlineData("vs_debug_get_state")]
    public void ReadOnlyTools_AreClassifiedReadOnly(string toolName)
    {
        Assert.True(ToolRisk.IsReadOnly(toolName));
    }

    [Theory]
    [InlineData("vs_edit_lines")]
    [InlineData("vs_apply_edits")]
    [InlineData("vs_append_file")]
    [InlineData("vs_create_file")]
    [InlineData("vs_replace_in_file")]
    [InlineData("vs_remove_file")]
    [InlineData("vs_run_command")]
    [InlineData("vs_build")]
    [InlineData("vs_run_tests")]
    [InlineData("vs_git_revert")]
    [InlineData("vs_checkpoint_create")]
    [InlineData("vs_checkpoint_rollback")]
    [InlineData("vs_ast_replace_method")]
    [InlineData("vs_ast_replace_member")]
    [InlineData("vs_ast_add_using")]
    [InlineData("vs_debug_evaluate")]
    [InlineData("vs_add_package")]
    public void MutatingTools_AreNotReadOnly(string toolName)
    {
        Assert.False(ToolRisk.IsReadOnly(toolName));
    }

    [Theory]
    [InlineData("mcp__server__some_tool")]
    [InlineData("vs_not_a_real_tool")]
    [InlineData("")]
    public void UnknownTools_AreNotReadOnly(string toolName)
    {
        Assert.False(ToolRisk.IsReadOnly(toolName));
    }
}