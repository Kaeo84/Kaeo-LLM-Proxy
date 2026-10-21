using System.Net;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Tests for compact endpoint functionality: the format enumeration, Copilot request detection,
/// the production token/budget estimators, and the <see cref="AutoCompactionService.ShouldCompact"/>
/// gate including its per-session circuit breaker. Structural assertions on the compacted bodies
/// themselves live in <see cref="CompactBehaviorTests"/>, which drives the real map-reduce pipeline
/// end-to-end against a stubbed upstream.
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

	// ── Context Window / Compaction Budget Tests ─────────────────────────

	[Fact]
	public void ContextWindowFraction_Is75Percent()
	{
		Assert.Equal(0.75, AutoCompactionService.ContextWindowFraction);
	}

	[Fact]
	public void GetSummaryPromptBudget_LargeWindow_ReservesRoomForTheReply()
	{
		// (int)(65536 * 0.75) = 49152 usable prompt+reply tokens; the default reply
		// reserve (DefaultSummaryMaxTokens = 1000) is held back for the summary itself.
		Assert.Equal(48152, AutoCompactionService.GetSummaryPromptBudget(65536));

		// A combine pass reserves the larger combine ceiling (CombineMaxTokens = 1500) instead of
		// the default: 49152 - 1500 = 47652. The expectation is a hand-computed literal so the
		// assertion pins the production value instead of re-deriving the production formula.
		Assert.Equal(47652,
			AutoCompactionService.GetSummaryPromptBudget(65536, AutoCompactionService.CombineMaxTokens));
	}

	[Fact]
	public void GetSummaryPromptBudget_TinyWindow_FloorsAtTheMinimumPrompt()
	{
		// 512 * 0.75 - 1000 goes negative, so the minimum prompt floor wins.
		Assert.Equal(AutoCompactionService.MinSummaryPromptTokens,
			AutoCompactionService.GetSummaryPromptBudget(512));
	}

	[Fact]
	public void EffectiveSummaryMaxTokensFor_RaisesSmallCeilingsToTheDefault()
	{
		Assert.Equal(AutoCompactionService.DefaultSummaryMaxTokens,
			AutoCompactionService.EffectiveSummaryMaxTokensFor(0));
		Assert.Equal(AutoCompactionService.DefaultSummaryMaxTokens,
			AutoCompactionService.EffectiveSummaryMaxTokensFor(500));
		Assert.Equal(2500,
			AutoCompactionService.EffectiveSummaryMaxTokensFor(2500));
	}

	// ── Token Estimation Tests ───────────────────────────────────────────

	[Fact]
	public void EstimateTokenCount_UsesTheFourCharactersPerTokenRule()
	{
		Assert.Equal(2, AutoCompactionService.EstimateTokenCount("Hello world")); // 11 chars / 4
		Assert.Equal(2500, AutoCompactionService.EstimateTokenCount(new string('x', 10_000)));
	}

	[Fact]
	public void EstimateTokenCount_EmptyBody_IsZero()
	{
		Assert.Equal(0, AutoCompactionService.EstimateTokenCount(string.Empty));
	}

	// ── ShouldCompact Gate Tests ─────────────────────────────────────────

	[Fact]
	public void ShouldCompact_DisabledPaths_ReturnsFalseEvenForHugeBody()
	{
		using HttpClient http = new();
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping();
		mapping.AutoCompactPaths = AutoCompactPaths.None;

		string body = BuildBody(("user", "question " + new string('a', 200_000)));

		Assert.False(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out _));
	}

	[Fact]
	public void ShouldCompact_BelowThreshold_ReturnsFalseWithoutSessionKey()
	{
		using HttpClient http = new();
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping(); // threshold: 40,000 tokens

		string body = BuildBody(("user", "hello"));

		Assert.False(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string sessionKey));
		Assert.Equal(string.Empty, sessionKey);
	}

	[Fact]
	public void ShouldCompact_AboveThreshold_ReturnsTrueWithStablePerConversationSessionKey()
	{
		using HttpClient http = new();
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping();

		// 200k chars ≈ 50k estimated tokens, above the 40k threshold.
		string body = BuildBody(("user", "question " + new string('a', 200_000)));

		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string first));
		Assert.False(string.IsNullOrWhiteSpace(first));

		// The same conversation must map to the same key so the circuit breaker is
		// per-conversation rather than per-request.
		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string second));
		Assert.Equal(first, second);

		// A different opening message is a different conversation and gets a different key.
		string other = BuildBody(("user", "different " + new string('a', 200_000)));
		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, other, out string third));
		Assert.NotEqual(first, third);
	}

	[Fact]
	public void ShouldCompact_BodyWithoutUserMessage_UsesModelScopedFallbackKey()
	{
		using HttpClient http = new();
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping();

		string body = BuildBody(("assistant", new string('a', 200_000)));

		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.OpenAI, body, out string sessionKey));
		Assert.Equal("big-model:unknown", sessionKey);
	}

	[Fact]
	public async Task ShouldCompact_ReturnsFalse_WhileTheSessionCircuitIsOpen()
	{
		FailingHandler stub = new();
		using HttpClient http = new(stub);
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping();
		string body = BuildBody(
			("user", "question " + new string('d', 200_000)),
			("user", "still here?"));

		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string sessionKey));

		// Three failed attempts accumulate without opening the breaker.
		for (int i = 0; i < 3; i++)
			Assert.Null(await CompactOnceAsync(service, mapping, body, sessionKey));

		// The fourth attempt trips the breaker and short-circuits without touching upstream.
		int callsBeforeFourth = stub.CallCount;
		Assert.Null(await CompactOnceAsync(service, mapping, body, sessionKey));
		Assert.Equal(callsBeforeFourth, stub.CallCount);

		// The gate now refuses this session.
		Assert.False(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out _));
	}

	[Fact]
	public async Task RecordSuccess_ReopensTheGate_AndTheNextCompactionReachesUpstreamAgain()
	{
		FailingHandler stub = new();
		using HttpClient http = new(stub);
		AutoCompactionService service = new(http);
		ModelMapping mapping = NewMapping();
		string body = BuildBody(
			("user", "question " + new string('d', 200_000)),
			("user", "still here?"));

		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string sessionKey));

		for (int i = 0; i < 4; i++)
			Assert.Null(await CompactOnceAsync(service, mapping, body, sessionKey));
		Assert.False(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out _));

		service.RecordSuccess(sessionKey);

		Assert.True(service.ShouldCompact(mapping, AutoCompactPaths.Ollama, body, out string reopenedKey));
		Assert.Equal(sessionKey, reopenedKey);

		int callsBeforeRetry = stub.CallCount;
		Assert.Null(await CompactOnceAsync(service, mapping, body, sessionKey));
		Assert.True(stub.CallCount > callsBeforeRetry,
			"A reset breaker must let the next attempt reach the upstream instead of short-circuiting.");
	}

	// ── Helpers ──────────────────────────────────────────────────────────

	/// <summary>Upstream stub that always fails, to exercise circuit-breaker behavior.</summary>
	private sealed class FailingHandler : HttpMessageHandler
	{
		public int CallCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			CallCount++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
			{
				Content = new StringContent("""{"error":{"message":"boom"}}""", Encoding.UTF8, "application/json"),
			});
		}
	}

	private static ModelMapping NewMapping() => new()
	{
		ProxyName = "big-model",
		ModelName = "upstream.gguf",
		UpstreamUrl = "http://localhost:8080",
		ContextWindowTokens = 65536,
		ProactiveOverflowTokens = 40000,
		AutoCompactPaths = AutoCompactPaths.Both,
	};

	private static string BuildBody(params (string Role, string Content)[] messages)
	{
		var list = messages.Select(m => new { role = m.Role, content = m.Content }).ToList();
		return JsonSerializer.Serialize(new { model = "upstream.gguf", messages = list });
	}

	private static Task<string?> CompactOnceAsync(
		AutoCompactionService service, ModelMapping mapping, string body, string sessionKey) =>
		service.CompactAsync(
			mapping, body, sessionKey, "http://localhost:8080", null, 30,
			1000, "upstream.gguf", 65536, 65536, CancellationToken.None);
}