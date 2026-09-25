using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Phase-B4 golden parity: the payload pipeline (RequestPayloadPipeline with its ordered steps) must
/// produce the same upstream-bound body as the legacy inline monolith
/// (OllamaProxyHandler.NormalizeRequestBody).
///
/// Comparison is semantic rather than textual: the legacy method emits a single-pass
/// <c>Utf8JsonWriter</c> body while the pipeline re-serializes a parsed object, so key order and
/// whitespace legitimately differ. What must match exactly is the set of values the upstream
/// receives, plus which members are present at all - that presence/absence is the part providers
/// actually react to.
/// </summary>
public class RequestPayloadParityTests
{
    /// <summary>Canonical form: recursively sorted keys, so key order is not part of the comparison.</summary>
    private static string Canonical(string json) => Canonicalize(JsonNode.Parse(json)!);

    private static string Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonicalize(pair.Value ?? JsonValue.Create((string?)null)!))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(item => item is null ? "null" : Canonicalize(item))) + "]",
        _ => node.ToJsonString(),
    };

    private static AppSettings SettingsWith(Action<AppSettings>? configure = null)
    {
        AppSettings settings = new();
        configure?.Invoke(settings);
        return settings;
    }

    private static ModelMapping AddMapping(AppSettings settings, string proxyName, Action<ModelMapping>? configure = null)
    {
        ModelMapping mapping = new()
        {
            ProxyName = proxyName,
            ModelName = proxyName + "-upstream",
            UpstreamUrl = "http://localhost:8080",
            IsEnabled = true,
        };

        configure?.Invoke(mapping);
        settings.ModelMappings.Add(mapping);
        return mapping;
    }

    private static string ChatBody(string model, params string[] rolesAndContents)
    {
        JsonArray messages = [];
        for (int i = 0; i + 1 < rolesAndContents.Length; i += 2)
            messages.Add(new JsonObject { ["role"] = rolesAndContents[i], ["content"] = rolesAndContents[i + 1] });

        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
        }.ToJsonString();
    }

    /// <summary>Runs both implementations and asserts the same upstream-bound body is produced.</summary>
    private static void AssertParity(string body, AppSettings settings, Action<RequestLog>? assertLog = null)
    {
        RequestLog legacyLog = new();
        string legacy = OllamaProxyHandler.NormalizeRequestBody(body, settings, legacyLog, null, out StreamOptionsInfo legacyOptions);

        RequestLog pipelineLog = new();
        RequestPayloadPipeline.Result pipeline = RequestPayloadPipeline.Run(
            body, settings, pipelineLog, RequestPayloadPipeline.DefaultSteps);

        Assert.Equal(Canonical(legacy), Canonical(pipeline.Body));
        Assert.Equal(legacyOptions, pipeline.StreamOptions);
        assertLog?.Invoke(pipelineLog);
    }

    [Fact]
    public void NoRewriteReturnsOriginalTextVerbatim()
    {
        AppSettings settings = SettingsWith();
        // ModelName matches the proxy name, so the model is not rewritten and nothing else applies.
        AddMapping(settings, "qwen", m => m.ModelName = "qwen");

        // Deliberately unusual formatting: no rewrite must happen, so this exact text must survive.
        string body = "{ \"model\" : \"qwen\" ,\n  \"messages\" : [ ] }";

        RequestPayloadPipeline.Result result = RequestPayloadPipeline.Run(
            body, settings, new RequestLog(), RequestPayloadPipeline.DefaultSteps);

        Assert.Equal(body, result.Body);
        Assert.Equal(StreamOptionsInfo.None, result.StreamOptions);
    }

    [Fact]
    public void MalformedBodyIsForwardedUnchanged()
    {
        AppSettings settings = SettingsWith();
        string body = "{ not json";

        RequestPayloadPipeline.Result result = RequestPayloadPipeline.Run(
            body, settings, new RequestLog(), RequestPayloadPipeline.DefaultSteps);

        Assert.Equal(body, result.Body);
    }

    [Fact]
    public void UnmappedModelMatchesLegacy()
        => AssertParity(ChatBody("unmapped", "user", "hi"), SettingsWith());

    [Fact]
    public void ModelRewriteMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "alias", m => m.ModelName = "real-upstream-model");

        AssertParity(ChatBody("alias", "user", "hi"), settings);
    }

    [Fact]
    public void ProxyTemperatureIsInjectedMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.TemperaturePriority = SamplingPriority.Proxy;
            mapping.Temperature = 0.25;
        });

        AssertParity(ChatBody("m", "user", "hi"), settings);
    }

    [Fact]
    public void ProviderPriorityDropsClientTemperatureMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.TemperaturePriority = SamplingPriority.Provider;
            mapping.RepeatPenaltyPriority = SamplingPriority.Provider;
        });

        string body = new JsonObject
        {
            ["model"] = "m",
            ["temperature"] = 0.9,
            ["repeat_penalty"] = 1.2,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    [Fact]
    public void ProxyOverwritesClientSamplingMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.TemperaturePriority = SamplingPriority.Proxy;
            mapping.Temperature = 0.1;
            mapping.RepeatPenaltyPriority = SamplingPriority.Proxy;
            mapping.RepeatPenalty = 1.05;
        });

        string body = new JsonObject
        {
            ["model"] = "m",
            ["temperature"] = 0.9,
            ["repeat_penalty"] = 1.2,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    [Theory]
    [InlineData((int)ReasoningEffortFormat.Legacy)]
    [InlineData((int)ReasoningEffortFormat.Modern)]
    [InlineData((int)ReasoningEffortFormat.QwenCloud)]
    [InlineData((int)ReasoningEffortFormat.ChatTemplateKwargs)]
    public void ReasoningEffortWireShapesMatchLegacy(int formatValue)
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.ReasoningEffortPriority = SamplingPriority.Proxy;
            mapping.ReasoningEffort = "High";
            mapping.ReasoningEffortFormat = (ReasoningEffortFormat)formatValue;
        });

        AssertParity(ChatBody("m", "user", "hi"), settings);
    }

    [Fact]
    public void ProviderPriorityDropsClientReasoningEffortMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.ReasoningEffortPriority = SamplingPriority.Provider);

        string body = new JsonObject
        {
            ["model"] = "m",
            ["reasoning_effort"] = "high",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    [Fact]
    public void StreamOptionsStrippedWhenCopilotCompatibleMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = new JsonObject
        {
            ["model"] = "m",
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings, log =>
        {
            // The response path needs to know the client asked for usage.
        });

        RequestLog log2 = new();
        RequestPayloadPipeline.Result result = RequestPayloadPipeline.Run(
            body, settings, log2, RequestPayloadPipeline.DefaultSteps);
        Assert.True(result.StreamOptions.Present);
        Assert.True(result.StreamOptions.IncludeUsage);
        Assert.True(result.StreamOptions.Stripped);
        Assert.True(result.StreamOptions.MustSynthesizeUsage);
    }

    [Fact]
    public void StreamOptionsPreservedWhenCompatibilityDisabledMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.EnableCopilotCompatibility = false);

        string body = new JsonObject
        {
            ["model"] = "m",
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings);

        RequestPayloadPipeline.Result result = RequestPayloadPipeline.Run(
            body, settings, new RequestLog(), RequestPayloadPipeline.DefaultSteps);
        Assert.False(result.StreamOptions.Stripped);
        Assert.False(result.StreamOptions.MustSynthesizeUsage);
    }

    [Fact]
    public void ConsecutiveLeadingSystemMessagesAreMergedMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        AssertParity(
            ChatBody("m", "system", "first", "system", "second", "user", "hi"),
            settings);
    }

    [Fact]
    public void InstructionTextIsInjectedMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        settings.InstructionSets.Add(new InstructionSet { Name = "MySet", Instructions = "Always be terse." });
        AddMapping(settings, "m", mapping => mapping.InstructionSetName = "MySet");

        AssertParity(ChatBody("m", "system", "client system", "user", "hi"), settings);
    }

    [Fact]
    public void TrailingAssistantPrefillIsRemovedMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.EnableThinkingCompatibility = true);

        AssertParity(ChatBody("m", "user", "hi", "assistant", "partial answer"), settings);
    }

    [Fact]
    public void AssistantPrefillWithToolCallsIsKeptMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.EnableThinkingCompatibility = true);

        string body = new JsonObject
        {
            ["model"] = "m",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "hi" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c1", ["type"] = "function" }),
                }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    [Fact]
    public void AllTransformsTogetherMatchLegacy()
    {
        AppSettings settings = SettingsWith();
        settings.InstructionSets.Add(new InstructionSet { Name = "Set", Instructions = "Be brief." });
        AddMapping(settings, "combo", mapping =>
        {
            mapping.ModelName = "combo-upstream";
            mapping.InstructionSetName = "Set";
            mapping.TemperaturePriority = SamplingPriority.Proxy;
            mapping.Temperature = 0.3;
            mapping.RepeatPenaltyPriority = SamplingPriority.Provider;
            mapping.ReasoningEffortPriority = SamplingPriority.Proxy;
            mapping.ReasoningEffort = "medium";
            mapping.ReasoningEffortFormat = ReasoningEffortFormat.Legacy | ReasoningEffortFormat.QwenCloud;
            mapping.EnableThinkingCompatibility = true;
        });

        string body = new JsonObject
        {
            ["model"] = "combo",
            ["temperature"] = 0.9,
            ["repeat_penalty"] = 1.3,
            ["reasoning_effort"] = "low",
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "s1" },
                new JsonObject { ["role"] = "system", ["content"] = "s2" },
                new JsonObject { ["role"] = "user", ["content"] = "hi" },
                new JsonObject { ["role"] = "assistant", ["content"] = "prefill" }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    [Fact]
    public void DebugModeRecordsTheDecisionsMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        settings.DebugMode = true;
        AddMapping(settings, "m", mapping =>
        {
            mapping.TemperaturePriority = SamplingPriority.Proxy;
            mapping.Temperature = 0.4;
        });

        RequestLog legacyLog = new();
        OllamaProxyHandler.NormalizeRequestBody(ChatBody("m", "user", "hi"), settings, legacyLog, null, out _);

        RequestLog pipelineLog = new();
        RequestPayloadPipeline.Run(ChatBody("m", "user", "hi"), settings, pipelineLog, RequestPayloadPipeline.DefaultSteps);

        Assert.False(string.IsNullOrWhiteSpace(legacyLog.DebugSummary));
        Assert.False(string.IsNullOrWhiteSpace(pipelineLog.DebugSummary));

        // Both report the same temperature decision.
        Assert.Contains("temperature", pipelineLog.DebugSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temperature", legacyLog.DebugSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseInsensitiveStreamOptionsMatchingMatchesLegacy()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        // Capitalized stream_options: both implementations match it case-insensitively and strip it.
        string body = new JsonObject
        {
            ["model"] = "m",
            ["Stream_Options"] = new JsonObject { ["Include_Usage"] = true },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        AssertParity(body, settings);
    }

    // ── Per-step isolation ────────────────────────────────────────────────
    //
    // A step that reports Applies without changing anything, or reports that it does not apply and
    // then changes something anyway, would break the fast path that keeps an unmodified request
    // byte-identical. These tests pin that contract step by step, so a later edit to one step fails
    // here precisely rather than only showing up as a whole-body parity failure.

    /// <summary>Runs one step alone and reports whether it claimed to apply and whether it changed anything.</summary>
    private static (bool Claimed, bool Changed) RunSingleStep(
        IRequestPayloadStep step, string body, AppSettings settings)
    {
        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = settings,
            Log = new RequestLog(),
        };

        string before = context.Payload.ToJsonString();

        bool claimed = step.Applies(context);
        if (claimed)
            step.Apply(context);

        return (claimed, !string.Equals(before, context.Payload.ToJsonString(), StringComparison.Ordinal));
    }

    private static string UnmappedBody()
        => new JsonObject
        {
            ["model"] = "unknown-model",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

    [Fact]
    public void SamplingStepDoesNotClaimForAnUnmappedModel()
    {
        (bool claimed, bool changed) = RunSingleStep(
            new SamplingPriorityStep(), UnmappedBody(), SettingsWith());

        Assert.False(claimed);
        Assert.False(changed);
    }

    [Fact]
    public void SamplingStepDoesNotClaimForClientAppPriority()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = new JsonObject
        {
            ["model"] = "m",
            ["temperature"] = 0.5,
            ["messages"] = new JsonArray(),
        }.ToJsonString();

        // ClientApp on both members means the client's own values are authoritative.
        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = settings,
            Log = new RequestLog(),
            Mapping = settings.FindModelMapping("m"),
        };

        Assert.False(new SamplingPriorityStep().Applies(context));
    }

    [Fact]
    public void SamplingStepInjectsADefaultWhenTheClientSentNothing()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.TemperaturePriority = SamplingPriority.Proxy;
            mapping.Temperature = 0.35;
        });

        (bool claimed, bool changed) = RunSingleStep(new SamplingPriorityStep(), UnmappedBody(), settings);

        // Unmapped model: the step looks up "unknown-model" and finds no mapping, so it must not
        // claim. This pins that the step reads the resolved mapping rather than a stray default.
        Assert.False(claimed);
        Assert.False(changed);
    }

    [Fact]
    public void ReasoningStepDoesNotClaimWithoutAConfiguredEffort()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping =>
        {
            mapping.ReasoningEffortPriority = SamplingPriority.Proxy;
            mapping.ReasoningEffort = null;
        });

        string body = new JsonObject { ["model"] = "m", ["messages"] = new JsonArray() }.ToJsonString();

        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = settings,
            Log = new RequestLog(),
            Mapping = settings.FindModelMapping("m"),
        };

        // Proxy priority with no value configured must not inject anything.
        Assert.False(new ReasoningEffortStep().Applies(context));
    }

    [Fact]
    public void StreamOptionsStepDoesNotClaimWhenTheBodyHasNoStreamOptions()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = new JsonObject { ["model"] = "m", ["messages"] = new JsonArray() }.ToJsonString();

        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = settings,
            Log = new RequestLog(),
            Mapping = settings.FindModelMapping("m"),
        };

        Assert.False(new StreamOptionsStep().Applies(context));
    }

    [Fact]
    public void ComposeMessagesStepDoesNotClaimForAWellFormedConversation()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m");

        string body = ChatBody("m", "system", "one", "user", "hi");

        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = settings,
            Log = new RequestLog(),
            Mapping = settings.FindModelMapping("m"),
        };

        // A single leading system message with no instruction set needs no recomposition.
        Assert.False(new ComposeMessagesStep().Applies(context));
    }

    [Fact]
    public void RewriteModelNameStepDoesNotClaimWhenNamesAlreadyMatch()
    {
        string body = UnmappedBody();

        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = SettingsWith(),
            Log = new RequestLog(),
            OriginalModel = "same",
            ResolvedModel = "same",
        };

        Assert.False(new RewriteModelNameStep().Applies(context));
    }

    [Fact]
    public void ResolveModelStepAlwaysClaimsSoLaterStepsHaveAMapping()
    {
        string body = UnmappedBody();

        RequestPayloadContext context = new()
        {
            OriginalJson = body,
            Payload = JsonNode.Parse(body)!.AsObject(),
            Settings = SettingsWith(),
            Log = new RequestLog(),
        };

        ResolveModelStep step = new();
        Assert.True(step.Applies(context));

        step.Apply(context);

        // It records the resolution without necessarily mutating the payload, which is why it must
        // claim unconditionally: the later steps depend on the values it sets.
        Assert.Equal("unknown-model", context.OriginalModel);
        Assert.Equal("unknown-model", context.EffectiveModel);
    }

    /// <summary>
    /// The legacy single-pass writer reads the client's <c>model</c> member case-sensitively
    /// (<c>TryGetProperty</c>) but rewrites it case-insensitively, so a client that capitalizes the
    /// member gets an empty model resolution yet a rewritten value. The pipeline reads it
    /// case-insensitively instead, which is the coherent behaviour. This test documents the
    /// deliberate divergence rather than pretending the two agree.
    /// </summary>
    [Fact]
    public void CapitalizedModelMemberDivergesFromLegacyDeliberately()
    {
        AppSettings settings = SettingsWith();
        AddMapping(settings, "m", mapping => mapping.ModelName = "m-upstream");

        string body = new JsonObject
        {
            ["Model"] = "m",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }.ToJsonString();

        RequestLog legacyLog = new();
        string legacy = OllamaProxyHandler.NormalizeRequestBody(body, settings, legacyLog, null, out _);

        RequestLog pipelineLog = new();
        RequestPayloadPipeline.Result pipeline = RequestPayloadPipeline.Run(
            body, settings, pipelineLog, RequestPayloadPipeline.DefaultSteps);

        // Legacy cannot read "Model", so it resolves no mapping and forwards the body untouched.
        Assert.Equal(body, legacy);

        // The pipeline reads it case-insensitively, rewrites the model, and records the model it
        // actually acted on rather than an empty one.
        Assert.Contains("m-upstream", pipeline.Body);
        Assert.Equal("m", pipelineLog.Model);
    }
}