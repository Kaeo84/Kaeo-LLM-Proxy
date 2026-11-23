using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the sub-agent delegation policy: honest refusals when disabled or at the depth
/// cap, tool scoping that never exceeds the parent's allowance, and vs_delegate exclusion.
/// </summary>
public class SubAgentPolicyTests
{
    private static readonly string[] AllTools =
        ["vs_get_file", "vs_build", "vs_run_command", "vs_delegate", "server-search"];

    [Fact]
    public void Validate_Disabled_RefusesWithConfigHint()
    {
        var refusal = SubAgentPolicy.Validate(enabled: false, depth: 0);
        Assert.NotNull(refusal);
        Assert.Contains("SubAgents", refusal);
    }

    [Fact]
    public void Validate_EnabledMainTurn_Allows()
    {
        Assert.Null(SubAgentPolicy.Validate(enabled: true, depth: 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Validate_AtOrBeyondDepthCap_Refuses(int depth)
    {
        var refusal = SubAgentPolicy.Validate(enabled: true, depth);
        Assert.NotNull(refusal);
        Assert.Contains("depth cap", refusal);
    }

    [Fact]
    public void Validate_DisabledWinsOverDepth()
    {
        var refusal = SubAgentPolicy.Validate(enabled: false, depth: 5);
        Assert.NotNull(refusal);
        Assert.Contains("disabled", refusal);
    }

    [Fact]
    public void ResolveTools_NoRequestedNoParent_AllToolsMinusDelegate()
    {
        var tools = SubAgentPolicy.ResolveTools(requested: null, parentTools: null, AllTools);
        Assert.Equal(["vs_get_file", "vs_build", "vs_run_command", "server-search"], tools);
    }

    [Fact]
    public void ResolveTools_ParentScopeOnly_UsesParentMinusDelegate()
    {
        var parent = new[] { "vs_get_file", "vs_delegate" };
        var tools = SubAgentPolicy.ResolveTools(requested: null, parent, AllTools);
        Assert.Equal(["vs_get_file"], tools);
    }

    [Fact]
    public void ResolveTools_RequestedWithoutParent_UsesRequestedMinusDelegate()
    {
        var requested = new[] { "vs_build", "vs_delegate", "vs_get_file" };
        var tools = SubAgentPolicy.ResolveTools(requested, parentTools: null, AllTools);
        Assert.Equal(["vs_build", "vs_get_file"], tools);
    }

    [Fact]
    public void ResolveTools_RequestedExceedingParent_ClippedToParentScope()
    {
        var parent = new[] { "vs_get_file", "vs_build" };
        var requested = new[] { "vs_get_file", "vs_run_command", "server-search" };
        var tools = SubAgentPolicy.ResolveTools(requested, parent, AllTools);
        Assert.Equal(["vs_get_file"], tools);
    }

    [Fact]
    public void ResolveTools_DelegateInParentScope_StillRemoved()
    {
        var parent = new[] { "vs_delegate", "vs_build" };
        var requested = new[] { "vs_delegate", "vs_build" };
        var tools = SubAgentPolicy.ResolveTools(requested, parent, AllTools);
        Assert.Equal(["vs_build"], tools);
    }

    [Fact]
    public void ResolveTools_DuplicatesAndBlanks_Cleaned()
    {
        var requested = new[] { "vs_build", "vs_build", "", "  ", "vs_get_file" };
        var tools = SubAgentPolicy.ResolveTools(requested, parentTools: null, AllTools);
        Assert.Equal(["vs_build", "vs_get_file"], tools);
    }

    [Fact]
    public void ResolveTools_EmptyRequested_EmptyScope()
    {
        var tools = SubAgentPolicy.ResolveTools([], parentTools: null, AllTools);
        Assert.Empty(tools);
    }

    [Fact]
    public void ResolveTools_UnknownRequestedName_PassesThroughForProjectionToDrop()
    {
        var requested = new[] { "not_a_real_tool" };
        var tools = SubAgentPolicy.ResolveTools(requested, parentTools: null, AllTools);
        Assert.Equal(["not_a_real_tool"], tools);
    }

    [Fact]
    public void ResolveTools_ParentIntersectionIsCaseSensitive()
    {
        var parent = new[] { "vs_build" };
        var requested = new[] { "VS_BUILD" };
        var tools = SubAgentPolicy.ResolveTools(requested, parent, AllTools);
        Assert.Empty(tools);
    }

    [Fact]
    public void ResolveTools_NullAllTools_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SubAgentPolicy.ResolveTools(null, null, null!));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(1, 1)]
    [InlineData(0, 25)]
    [InlineData(-3, 25)]
    public void ResolveBudget_PositiveConfiguredWins_ElseDefault(int configured, int expected)
    {
        Assert.Equal(expected, SubAgentPolicy.ResolveBudget(configured));
    }

    [Fact]
    public void Constants_MatchV1Spec()
    {
        Assert.Equal(1, SubAgentPolicy.MaxDepth);
        Assert.Equal(25, SubAgentPolicy.DefaultBudget);
        Assert.Equal("vs_delegate", SubAgentPolicy.DelegateToolName);
    }
}