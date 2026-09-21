using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the shared system-prompt composition rule every request path now routes through:
/// instruction text first, then each leading system message, folded into exactly one system
/// message. Strict chat templates (Qwen3) reject a second system message, so the count matters
/// as much as the content.
/// </summary>
public class SystemPromptComposerTests
{
    private const string Instructions = "PROXY-INSTRUCTIONS";

    private static List<LlamaCppMessage> Chat(params (string Role, string Content)[] messages) =>
        [.. messages.Select(m => new LlamaCppMessage(m.Role, m.Content))];

    private static AppSettings SettingsWithInstructions(string? instructionSetName)
    {
        AppSettings settings = new();
        settings.InstructionSets.Add(new InstructionSet { Name = "set-a", Instructions = Instructions });
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "test-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:8080",
            InstructionSetName = instructionSetName,
        });

        return settings;
    }

    private static List<JsonElement> NormalizeMessages(string body, AppSettings settings)
    {
        RequestLog log = new();
        string result = OllamaProxyHandler.NormalizeRequestBody(body, settings, log);

        using JsonDocument doc = JsonDocument.Parse(result);
        List<JsonElement> messages = [.. doc.RootElement.GetProperty("messages").EnumerateArray()];

        return [.. messages.Select(m => m.Clone())];
    }

    // ── The merge rule itself ──────────────────────────────────────────────

    [Fact]
    public void MergePutsInstructionsBeforeTheClientSystemPrompt()
    {
        string merged = SystemPromptComposer.Merge(Instructions, ["CLIENT-SYSTEM"]);

        Assert.Equal("PROXY-INSTRUCTIONS\n\nCLIENT-SYSTEM", merged);
    }

    [Fact]
    public void MergePreservesEveryLeadingSystemMessageInOrder()
    {
        string merged = SystemPromptComposer.Merge(Instructions, ["FIRST", "SECOND", "THIRD"]);

        Assert.Equal("PROXY-INSTRUCTIONS\n\nFIRST\n\nSECOND\n\nTHIRD", merged);
    }

    [Fact]
    public void MergeReturnsJustTheClientSystemWhenNoInstructionsAreConfigured()
    {
        string merged = SystemPromptComposer.Merge(null, ["CLIENT-SYSTEM"]);

        Assert.Equal("CLIENT-SYSTEM", merged);
    }

    [Fact]
    public void MergeReturnsJustTheInstructionsWhenTheClientSentNoSystemPrompt()
    {
        string merged = SystemPromptComposer.Merge(Instructions, []);

        Assert.Equal(Instructions, merged);
    }

    [Fact]
    public void MergeReturnsEmptyWhenThereIsNothingToMerge()
    {
        string merged = SystemPromptComposer.Merge(null, []);

        Assert.Equal(string.Empty, merged);
    }

    [Theory]
    [InlineData("system", true)]
    [InlineData("SYSTEM", true)]
    [InlineData("System", true)]
    [InlineData("user", false)]
    [InlineData("assistant", false)]
    [InlineData(null, false)]
    public void IsSystemRoleMatchesTheSystemRoleCaseInsensitively(string? role, bool expected)
    {
        Assert.Equal(expected, SystemPromptComposer.IsSystemRole(role));
    }

    [Fact]
    public void ShouldRecomposeIsTrueWhenInstructionsAreConfigured()
    {
        Assert.True(SystemPromptComposer.ShouldRecompose(Instructions, 0));
    }

    [Fact]
    public void ShouldRecomposeIsTrueWhenTheClientSentSeveralLeadingSystemMessages()
    {
        Assert.True(SystemPromptComposer.ShouldRecompose(null, 2));
    }

    [Fact]
    public void ShouldRecomposeIsFalseForASingleSystemMessageAndNoInstructions()
    {
        // A lone client system message is forwarded verbatim so extra properties it carries survive.
        Assert.False(SystemPromptComposer.ShouldRecompose(null, 1));
    }

    [Fact]
    public void LeadingSystemCountStopsAtTheFirstNonSystemMessage()
    {
        int count = SystemPromptComposer.LeadingSystemCount(
            Chat(("system", "a"), ("system", "b"), ("user", "c"), ("system", "d")));

        Assert.Equal(2, count);
    }

    [Fact]
    public void LeadingSystemCountIsZeroWhenTheConversationOpensWithAUserMessage()
    {
        int count = SystemPromptComposer.LeadingSystemCount(
            Chat(("user", "hi"), ("system", "late")));

        Assert.Equal(0, count);
    }

    // ── The /v1/* passthrough path ─────────────────────────────────────────

    [Fact]
    public void PassthroughInjectsInstructionsIntoASingleSystemMessage()
    {
        List<JsonElement> messages = NormalizeMessages(
            """{"model":"test-model","messages":[{"role":"system","content":"CLIENT-SYSTEM"},{"role":"user","content":"hi"}]}""",
            SettingsWithInstructions("set-a"));

        Assert.Equal(2, messages.Count);
        Assert.Equal("PROXY-INSTRUCTIONS\n\nCLIENT-SYSTEM", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void PassthroughDoesNotDuplicateInstructionsAcrossRequests()
    {
        AppSettings settings = SettingsWithInstructions("set-a");
        List<JsonElement> messages = NormalizeMessages(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}]}""",
            settings);

        Assert.Single(
            messages,
            m => m.GetProperty("role").GetString() == "system");
        Assert.Equal(Instructions, messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void PassthroughFoldsMultipleLeadingSystemMessagesIntoOne()
    {
        List<JsonElement> messages = NormalizeMessages(
            """{"model":"test-model","messages":[{"role":"system","content":"A"},{"role":"system","content":"B"},{"role":"user","content":"hi"}]}""",
            SettingsWithInstructions(instructionSetName: null));

        Assert.Equal(2, messages.Count);
        Assert.Equal("A\n\nB", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void PassthroughLeavesAMidConversationSystemMessageAlone()
    {
        // The conversation deliberately does not end on an assistant message: a trailing assistant
        // turn is removed as a response prefill under thinking compatibility, which is a separate
        // concern from system-prompt composition.
        List<JsonElement> messages = NormalizeMessages(
            """{"model":"test-model","messages":[{"role":"system","content":"A"},{"role":"assistant","content":"ok"},{"role":"system","content":"LATER"},{"role":"user","content":"hi"}]}""",
            SettingsWithInstructions(instructionSetName: null));

        Assert.Equal(4, messages.Count);
        Assert.Equal("LATER", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public void PassthroughForwardsASingleSystemMessageVerbatimWhenNoInstructionsAreConfigured()
    {
        // No rewrite is needed, so the client's own object survives untouched.
        List<JsonElement> messages = NormalizeMessages(
            """{"model":"test-model","messages":[{"role":"system","content":"A"},{"role":"user","content":"hi"}]}""",
            SettingsWithInstructions(instructionSetName: null));

        Assert.Equal(2, messages.Count);
        Assert.Equal("A", messages[0].GetProperty("content").GetString());
    }

    // ── The /api/chat mapping rule (same composer, message list) ───────────

    [Fact]
    public void ChatCompositionFoldsInstructionsAndLeadingSystemMessagesIntoOneMessage()
    {
        List<LlamaCppMessage> messages = Chat(("system", "A"), ("system", "B"), ("user", "hi"));

        int leading = SystemPromptComposer.LeadingSystemCount(messages);
        string merged = SystemPromptComposer.Merge(Instructions, SystemPromptComposer.LeadingSystemContents(messages, leading));
        messages.RemoveRange(0, leading);
        messages.Insert(0, new LlamaCppMessage("system", merged));

        Assert.Equal(2, messages.Count);
        Assert.Equal("PROXY-INSTRUCTIONS\n\nA\n\nB", messages[0].Content);
    }

    [Fact]
    public void ChatCompositionLeavesTheConversationUntouchedWithoutInstructionsOrExtraSystemMessages()
    {
        List<LlamaCppMessage> messages = Chat(("system", "A"), ("user", "hi"));

        Assert.False(SystemPromptComposer.ShouldRecompose(null, SystemPromptComposer.LeadingSystemCount(messages)));
        Assert.Equal(2, messages.Count);
    }
}
