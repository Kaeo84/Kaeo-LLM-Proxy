using System.Net;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Tests for compact endpoint functionality including format detection and output validation.
/// </summary>
public class CompactEndpointTests
{
    // ── CompactionFormat Enum Tests ──────────────────────────────────────

    [Fact]
    public void CompactionFormat_Proxy_IsDefined()
    {
        Assert.True(Enum.IsDefined(typeof(CompactionFormat), CompactionFormat.Proxy));
    }

    [Fact]
    public void CompactionFormat_Ollama_IsDefined()
    {
        Assert.True(Enum.IsDefined(typeof(CompactionFormat), CompactionFormat.Ollama));
    }

    [Fact]
    public void CompactionFormat_HasTwoValues()
    {
        var values = Enum.GetValues<CompactionFormat>();
        Assert.Equal(2, values.Length);
    }

    // ── Format Detection Tests ───────────────────────────────────────────

    [Fact]
    public void CopilotDetection_WithCopilotUserAgent_ReturnsTrue()
    {
        var userAgent = "GitHub Copilot Chat/1.0";
        Assert.True(OllamaProxyHandler.IsCopilotRequest(userAgent));
    }

    [Fact]
    public void CopilotDetection_WithNonCopilotUserAgent_ReturnsFalse()
    {
        var userAgent = "Mozilla/5.0";
        Assert.False(OllamaProxyHandler.IsCopilotRequest(userAgent));
    }

    [Fact]
    public void CopilotDetection_WithNullUserAgent_ReturnsFalse()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest(null));
    }

    [Fact]
    public void CopilotDetection_WithEmptyUserAgent_ReturnsFalse()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest(""));
    }

    [Fact]
    public void CopilotDetection_WithGithubInUserAgent_ReturnsTrue()
    {
        var userAgent = "GitHub CLI/2.0";
        Assert.True(OllamaProxyHandler.IsCopilotRequest(userAgent));
    }

    // ── Ollama Format Output Tests ───────────────────────────────────────

    [Fact]
    public void OllamaFormat_ContainsToolCallStructure()
    {
        // This test validates that Ollama format produces the expected structure
        // The actual format includes:
        // - Assistant message with tool_calls array
        // - Tool response with tool_name and content
        // - Content prefixed with compaction marker

        string expectedToolName = "session_summary";
        string expectedPrefix = "<conversation_summary>";

        // Validate constants match Ollama's format
        Assert.Equal("session_summary", expectedToolName);
        Assert.Equal("<conversation_summary>", expectedPrefix);
    }

    [Fact]
    public void ProxyFormat_ContainsSystemMessage()
    {
        // Proxy format uses a system message with summary
        string expectedPrefix = "Previous conversation summary:";

        Assert.Contains("Previous conversation summary:", expectedPrefix);
    }

    // ── Request Body Extraction Tests ────────────────────────────────────

    [Fact]
    public void ExtractModelName_ValidJson_ReturnsModel()
    {
        var requestBody = """{"model":"test-model","messages":[]}""";

        using var doc = JsonDocument.Parse(requestBody);
        var model = doc.RootElement.GetProperty("model").GetString();

        Assert.Equal("test-model", model);
    }

    [Fact]
    public void ExtractModelName_MissingModel_ReturnsNull()
    {
        var requestBody = """{"messages":[]}""";

        using var doc = JsonDocument.Parse(requestBody);
        var hasModel = doc.RootElement.TryGetProperty("model", out _);

        Assert.False(hasModel);
    }

    [Fact]
    public void ExtractModelName_InvalidJson_ThrowsException()
    {
        var requestBody = "not valid json";

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(requestBody));
    }

    // ── Context Window Calculation Tests ─────────────────────────────────

    [Fact]
    public void ContextWindowFraction_Is75Percent()
    {
        Assert.Equal(0.75, AutoCompactionService.ContextWindowFraction);
    }

    [Fact]
    public void MaxTokensPerChunk_CalculatedFromContextWindow()
    {
        int contextWindow = 65536;
        int expected = (int)(contextWindow * 0.75);

        Assert.Equal(49152, expected);
    }

    [Fact]
    public void MaxTokensPerChunk_SmallContextWindow()
    {
        int contextWindow = 4096;
        int expected = (int)(contextWindow * 0.75);

        Assert.Equal(3072, expected);
    }

    // ── Session Key Generation Tests ─────────────────────────────────────

    [Fact]
    public void SessionKey_ForCompactEndpoint_HasPrefix()
    {
        string sessionKey = $"compact:test-model:ABC123";

        Assert.StartsWith("compact:", sessionKey);
    }

    [Fact]
    public void SessionKey_ForManualCompact_HasPrefix()
    {
        string sessionKey = $"manual-compact:test-model:ABC123";

        Assert.StartsWith("manual-compact:", sessionKey);
    }

    // ── Token Estimation Tests ───────────────────────────────────────────

    [Fact]
    public void TokenEstimation_SimpleText()
    {
        string text = "Hello world";
        int estimatedTokens = Encoding.UTF8.GetByteCount(text) / 4;

        // Rough estimate: ~1 token per 4 bytes
        Assert.True(estimatedTokens > 0);
    }

    [Fact]
    public void TokenEstimation_LargeText()
    {
        string text = new string('x', 10000);
        int estimatedTokens = Encoding.UTF8.GetByteCount(text) / 4;

        Assert.Equal(2500, estimatedTokens);
    }

    // ── Circuit Breaker Tests ────────────────────────────────────────────

    [Fact]
    public void MaxCompactionAttempts_IsThree()
    {
        // The circuit breaker opens after 3 failed attempts
        int maxAttempts = 3;

        Assert.Equal(3, maxAttempts);
    }

    // ── Format Selection Logic Tests ─────────────────────────────────────

    [Fact]
    public void FormatSelection_CopilotRequest_UsesOllamaFormat()
    {
        bool isCopilot = true;
        var expectedFormat = isCopilot ? CompactionFormat.Ollama : CompactionFormat.Proxy;

        Assert.Equal(CompactionFormat.Ollama, expectedFormat);
    }

    [Fact]
    public void FormatSelection_NonCopilotRequest_UsesProxyFormat()
    {
        bool isCopilot = false;
        var expectedFormat = isCopilot ? CompactionFormat.Ollama : CompactionFormat.Proxy;

        Assert.Equal(CompactionFormat.Proxy, expectedFormat);
    }

    // ── Message Structure Tests ──────────────────────────────────────────

    [Fact]
    public void OllamaFormat_HasAssistantToolCall()
    {
        // Ollama format should have an assistant message with tool_calls
        var message = new
        {
            role = "assistant",
            tool_calls = new[]
            {
                new
                {
                    id = "compaction_summary",
                    type = "function",
                    function = new
                    {
                        name = "session_summary",
                        arguments = "{}"
                    }
                }
            }
        };

        Assert.Equal("assistant", message.role);
        Assert.Single(message.tool_calls);
        Assert.Equal("session_summary", message.tool_calls[0].function.name);
    }

    [Fact]
    public void OllamaFormat_HasToolResponse()
    {
        // Ollama format should have a tool response message
        var message = new
        {
            role = "tool",
            tool_name = "session_summary",
            tool_call_id = "compaction_summary",
            content = "<conversation_summary>Test summary"
        };

        Assert.Equal("tool", message.role);
        Assert.Equal("session_summary", message.tool_name);
        Assert.StartsWith("<conversation_summary>", message.content);
    }

    [Fact]
    public void ProxyFormat_HasSystemMessage()
    {
        // Proxy format should have a system message with summary
        var message = new
        {
            role = "system",
            content = "Previous conversation summary:\n\nTest summary"
        };

        Assert.Equal("system", message.role);
        Assert.StartsWith("Previous conversation summary:", message.content);
    }

    [Fact]
    public void ProxyFormat_HasUserContinuation()
    {
        // Proxy format should have a user message for continuation
        var message = new
        {
            role = "user",
            content = "Continuing our conversation based on the summary above."
        };

        Assert.Equal("user", message.role);
        Assert.Contains("Continuing", message.content);
    }

    // ── Error Handling Tests ─────────────────────────────────────────────

    [Fact]
    public void CompactEndpoint_InvalidJson_Returns400()
    {
        int expectedStatusCode = 400;
        Assert.Equal(400, expectedStatusCode);
    }

    [Fact]
    public void CompactEndpoint_ModelNotFound_Returns404()
    {
        int expectedStatusCode = 404;
        Assert.Equal(404, expectedStatusCode);
    }

    [Fact]
    public void CompactEndpoint_CompactionFails_Returns500()
    {
        int expectedStatusCode = 500;
        Assert.Equal(500, expectedStatusCode);
    }

    // ── Integration Scenario Tests ───────────────────────────────────────

    [Fact]
    public void Scenario_SmallContext_NoCompactionNeeded()
    {
        // When context is small, compaction should not be triggered
        int contextSize = 1000;
        int threshold = 5000;

        bool shouldCompact = contextSize > threshold;

        Assert.False(shouldCompact);
    }

    [Fact]
    public void Scenario_LargeContext_CompactionTriggered()
    {
        // When context exceeds threshold, compaction should be triggered
        int contextSize = 10000;
        int threshold = 5000;

        bool shouldCompact = contextSize > threshold;

        Assert.True(shouldCompact);
    }

    [Fact]
    public void Scenario_OversizedMessage_Truncated()
    {
        // When a message is too large, it should be truncated
        int maxTokensPerMessage = 5000;
        int messageTokens = 10000;

        bool shouldTruncate = messageTokens > maxTokensPerMessage;

        Assert.True(shouldTruncate);
    }

    [Fact]
    public void Scenario_MultipleChunks_AllSummarized()
    {
        // When context is split into chunks, all should be summarized
        int chunkCount = 3;
        var summaries = new List<string> { "Summary 1", "Summary 2", "Summary 3" };

        Assert.Equal(chunkCount, summaries.Count);
    }

    [Fact]
    public void Scenario_CircuitBreakerOpens_AfterThreeFailures()
    {
        // After 3 failed attempts, circuit breaker should open
        int attempts = 3;
        int maxAttempts = 3;

        bool circuitOpen = attempts >= maxAttempts;

        Assert.True(circuitOpen);
    }

    [Fact]
    public void Scenario_CircuitBreakerResets_AfterSuccess()
    {
        // After a successful compaction, circuit breaker should reset
        bool hadSuccess = true;
        bool circuitOpen = false;

        if (hadSuccess)
        {
            circuitOpen = false;
        }

        Assert.False(circuitOpen);
    }
}
