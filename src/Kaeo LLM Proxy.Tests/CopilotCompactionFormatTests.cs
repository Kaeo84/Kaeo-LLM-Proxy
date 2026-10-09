using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins that a Copilot compaction run is summarized with a prompt that defers to Copilot's own output
/// format instead of competing with it.
/// </summary>
/// <remarks>
/// The failure this exists because of: GitHub's <c>/compact</c> system prompt demands a specific
/// <c>&lt;ConversationSummary&gt;</c> XML skeleton and is preserved verbatim into the compacted body,
/// immediately before the summary text. That instruction outranks a generic prompt, so a prompt asking
/// for plain prose competes with it. The observed response filled the first two sections and left every
/// later one holding the template's own placeholder text.
/// </remarks>
public class CopilotCompactionFormatTests
{
    /// <summary>A stand-in for Copilot's /compact system prompt, carrying the detection signature.</summary>
    private const string CopilotCompactPrompt =
        "Your task is to **produce an authoritative, self-contained summary** of the current session as a "
        + "project checkpoint. Structure your summary using the exact <ConversationSummary> XML format "
        + "provided in the system message.";

    private static ModelMapping Mapping()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
        };
        mapping.EnsureId();
        return mapping;
    }

    private static string BuildBody(string firstRole, string firstContent)
    {
        var messages = new List<object>
        {
            new { role = firstRole, content = firstContent },
            new { role = "user", content = "earlier ask" },
            new { role = "assistant", content = "earlier answer" },
            new { role = "user", content = "latest ask" },
        };
        return JsonSerializer.Serialize(new { model = "main", messages });
    }

    /// <summary>Captures the summarizer system prompt sent upstream.</summary>
    private sealed class PromptCaptureHandler : HttpMessageHandler
    {
        private readonly List<string> _systemPrompts = [];

        public IReadOnlyList<string> SystemPrompts => _systemPrompts;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out JsonElement messages))
            {
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("role", out JsonElement role)
                        && role.GetString() == "system"
                        && message.TryGetProperty("content", out JsonElement content))
                    {
                        _systemPrompts.Add(content.GetString() ?? string.Empty);
                    }
                }
            }

            string reply = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = "Summary.\n\n## Tool activity\n- none" } } },
            });

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
        /// Runs a compaction with the Copilot flag set exactly as the handler sets it, and returns the
        /// summarizer prompts that reached the upstream.
        /// </summary>
        /// <remarks>
        /// The flag is derived through the handler's own detection on the same body, so the test cannot pass
        /// while production detection is broken — which is how the first version of this test failed, by
        /// calling the service directly and leaving the flag at its false default.
        /// </remarks>
        private static async Task<IReadOnlyList<string>> SummarizerPromptsForAsync(
            string firstRole, string firstContent, string? configuredInstructions = null)
        {
            PromptCaptureHandler capture = new();
            using HttpClient http = new(capture);
            AutoCompactionService service = new(http);
            service.Configure(configuredInstructions, 0);

            string body = BuildBody(firstRole, firstContent);

            await service.CompactAsync(
                Mapping(),
                body,
                "session",
                "http://localhost:8080",
                null,
                30,
                maxTokensPerChunk: 2000,
                "compact-model",
                100_000,
                100_000,
                CancellationToken.None,
                CompactionFormat.Proxy,
                isCopilotSummary: OllamaProxyHandler.IsCopilotCompactionBody(body));

            return capture.SystemPrompts;
        }

        // ── The detection the prompt choice depends on ────────────────────────

        [Fact]
        public void TheCopilotSignatureIsDetectedOnTheBodyBeingCompacted()
        {
            // The prompt choice is only as good as this detection, so pin it on the real prompt shape used
            // by the other Copilot tests: "**produce an authoritative, self-contained summary**".
            string body = BuildBody("system", CopilotCompactPrompt);

            Assert.True(OllamaProxyHandler.IsCopilotCompactionBody(body));
        }

        [Fact]
        public void AnOrdinaryConversationIsNotMistakenForCopilotCompaction()
        {
            Assert.False(OllamaProxyHandler.IsCopilotCompactionBody(
                BuildBody("system", "You are a helpful coding assistant.")));
        }

        [Fact]
        public void AnUnparseableBodyIsNotMistakenForCopilotCompaction()
        {
            // A malformed body must not throw on a path that runs for every compaction.
            Assert.False(OllamaProxyHandler.IsCopilotCompactionBody("not json"));
        }

    // ── The Copilot prompt defers to Copilot's format ─────────────────────

    [Fact]
    public async Task ACopilotCompactionUsesAPromptThatDefersToTheClientsFormat()
    {
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync("system", CopilotCompactPrompt);

        string prompt = Assert.Single(prompts);

        // It must point at the format the client already supplied, not invent a competing one.
        Assert.Contains("system message specifies", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("format", prompt, StringComparison.OrdinalIgnoreCase);

        // And it must address the observed failure: sections left as placeholder text.
        Assert.Contains("placeholder", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheCopilotPromptDoesNotForbidCopilotsOwnXml()
    {
        // The generic prompt avoids the XML tokens on the theory that emitting one would make the next
        // turn look like a fresh compaction request. That caution applies to what the summary contains,
        // not to whether the model may follow the client's skeleton — and for Copilot the skeleton is
        // mandatory. Nothing in the prompt may tell the model to avoid XML.
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync("system", CopilotCompactPrompt);

        string prompt = Assert.Single(prompts);

        Assert.DoesNotContain("avoid <ConversationSummary>", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do not output xml", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheCopilotPromptOmitsTheToolActivityHeading()
    {
        // Copilot's skeleton has no section for it, and demanding a heading the template lacks invites
        // text outside the XML, which the client explicitly forbids.
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync("system", CopilotCompactPrompt);

        string prompt = Assert.Single(prompts);

        Assert.DoesNotContain("## Tool activity", prompt);
    }

    // ── Other clients are unaffected ──────────────────────────────────────

    [Fact]
    public async Task AnOrdinaryCompactionKeepsTheGenericPrompt()
    {
        // The Copilot prompt describes the client, not the installation. Ordinary traffic must keep the
        // generic instructions, including the tool-activity requirement.
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync("system", "You are a helpful assistant.");

        string prompt = Assert.Single(prompts);

        Assert.Contains("## Tool activity", prompt);
        Assert.DoesNotContain("GitHub Copilot", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AConversationWithNoCopilotSignatureKeepsTheGenericPrompt()
    {
        // Detection is role-blind: it matches the signature in the first message's content, whatever role
        // that message carries. What actually selects the generic prompt is the absence of a signature.
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync("system", "You are a helpful coding assistant.");

        string prompt = Assert.Single(prompts);

        Assert.Contains("## Tool activity", prompt);
        Assert.DoesNotContain("GitHub Copilot", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSignatureIsMatchedRegardlessOfTheFirstMessageRole()
    {
        // Pins the role-blind behaviour the test above relies on, so a future change to make detection
        // role-aware is caught here rather than silently altering which prompt Copilot gets.
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest(CopilotCompactPrompt));
    }

    // ── An explicit instruction set still wins ────────────────────────────

    [Fact]
    public async Task AConfiguredInstructionSetOverridesTheCopilotPrompt()
    {
        // An operator who wrote their own prompt for a Copilot-facing deployment has stated what they
        // want. Silently replacing it would be worse than the format mismatch the default avoids.
        IReadOnlyList<string> prompts = await SummarizerPromptsForAsync(
            "system", CopilotCompactPrompt, configuredInstructions: "MY DELIBERATE PROMPT");

        string prompt = Assert.Single(prompts);

        Assert.Contains("MY DELIBERATE PROMPT", prompt);
        Assert.DoesNotContain("GitHub Copilot", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheConfiguredInstructionSetAlsoGetsTheCeilingAppended()
    {
        // The advisory ceiling applies regardless of which prompt is in force.
        PromptCaptureHandler capture = new();
        using HttpClient http = new(capture);
        AutoCompactionService service = new(http);
        service.Configure("MY PROMPT", 6000);

        await service.CompactAsync(
            Mapping(),
            BuildBody("system", "You are a helpful assistant."),
            "session",
            "http://localhost:8080",
            null,
            30,
            2000,
            "compact-model",
            100_000,
            100_000,
            CancellationToken.None);

        string prompt = Assert.Single(capture.SystemPrompts);
        Assert.Contains("MY PROMPT", prompt);
        Assert.Contains("6,000 tokens", prompt);
    }
}