using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the human-readable override audit trail lines produced by
/// <see cref="DebugNotes"/> for the debug-mode (before/after translation) logging feature.
/// </summary>
public class DebugNotesTests
{
    [Fact]
    public void ModelResolutionRewritesWhenMapped()
    {
        string line = DebugNotes.ModelResolution("llama3", "llama-3-8b", mapped: true);
        Assert.Contains("llama3", line);
        Assert.Contains("llama-3-8b", line);
    }

    [Fact]
    public void ModelResolutionPassesThroughWhenUnmapped()
    {
        string line = DebugNotes.ModelResolution("gpt-5", "gpt-5", mapped: false);
        Assert.Contains("passed through", line);
    }

    [Fact]
    public void ProxyPriorityReplacesClientTemperature()
    {
        string line = DebugNotes.SamplingDecision("temperature", SamplingPriority.Proxy, 0.5f, 0.7f);
        Assert.Contains("replaced client value 0.5", line);
        Assert.Contains("proxy override", line);
    }

    [Fact]
    public void ProxyPriorityInjectsRepeatPenaltyWhenClientSentNone()
    {
        string line = DebugNotes.SamplingDecision("repeat_penalty", SamplingPriority.Proxy, null, 1.1f);
        Assert.Contains("injected", line);
        Assert.Contains("1.1", line);
    }

    [Fact]
    public void ProviderPriorityOmitsField()
    {
        string line = DebugNotes.SamplingDecision("temperature", SamplingPriority.Provider, 0.9f, 0.7f);
        Assert.Contains("omitted", line);
        Assert.Contains("provider priority", line);
    }

    [Fact]
    public void ClientAppPriorityPassesClientValueThrough()
    {
        string line = DebugNotes.SamplingDecision("temperature", SamplingPriority.ClientApp, 0.3f, 0.7f);
        Assert.Contains("client value passed through", line);
        Assert.Contains("0.3", line);
    }

    [Fact]
    public void ClientAppPriorityNotesWhenClientSentNone()
    {
        string line = DebugNotes.SamplingDecision("temperature", SamplingPriority.ClientApp, null, 0.7f);
        Assert.Contains("not set", line);
    }

    [Fact]
    public void InstructionInjectionNamesTheSet()
    {
        string line = DebugNotes.InstructionInjection("MySet");
        Assert.Contains("MySet", line);
        Assert.Contains("injected", line);
    }

    [Fact]
    public void ReasoningEffortProxyPriorityInjectsViaMultipleFormats()
    {
        string line = DebugNotes.ReasoningEffortDecision(
            SamplingPriority.Proxy,
            clientEffort: null,
            proxyEffort: "high",
            format: ReasoningEffortFormat.Legacy | ReasoningEffortFormat.Modern);

        Assert.Contains("high", line);
        Assert.Contains("injected", line);
        Assert.Contains("reasoning_effort", line);
        Assert.Contains("reasoning.enable+thinking_level", line);
    }

    [Fact]
    public void ReasoningEffortProxyPriorityReplacesClientValue()
    {
        string line = DebugNotes.ReasoningEffortDecision(
            SamplingPriority.Proxy,
            clientEffort: "low",
            proxyEffort: "max",
            format: ReasoningEffortFormat.Legacy);

        Assert.Contains("replaced client 'low'", line);
    }

    [Fact]
    public void ReasoningEffortProviderPriorityDropsClientValue()
    {
        string line = DebugNotes.ReasoningEffortDecision(
            SamplingPriority.Provider,
            clientEffort: "medium",
            proxyEffort: null,
            format: ReasoningEffortFormat.Legacy);

        Assert.Contains("omitted", line);
        Assert.Contains("provider priority", line);
    }

    [Fact]
    public void ReasoningEffortClientAppPriorityPassesThrough()
    {
        string line = DebugNotes.ReasoningEffortDecision(
            SamplingPriority.ClientApp,
            clientEffort: "medium",
            proxyEffort: null,
            format: ReasoningEffortFormat.Legacy);

        Assert.Contains("client value passed through", line);
    }

    [Fact]
    public void NormalizeRequestBodyDebugModePopulatesDebugSummary()
    {
        // A Proxy-priority mapping that overrides the client's temperature and injects
        // reasoning effort must produce a debug summary when DebugMode is enabled.
        AppSettings settings = new();
        settings.DebugMode = true;
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "test-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:8080",
            TemperaturePriority = SamplingPriority.Proxy,
            Temperature = 0.7,
            ReasoningEffortPriority = SamplingPriority.Proxy,
            ReasoningEffort = "high",
            ReasoningEffortFormat = ReasoningEffortFormat.Legacy,
        });

        RequestLog log = new();
        string result = OllamaProxyHandler.NormalizeRequestBody(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            settings,
            log);

        Assert.NotNull(log.DebugSummary);
        Assert.Contains("temperature", log.DebugSummary);
        Assert.Contains("reasoning_effort", log.DebugSummary);

        // The upstream body still carries the proxy-injected values.
        JsonElement root = JsonDocument.Parse(result).RootElement;
        Assert.Equal(0.7f, root.GetProperty("temperature").GetSingle(), precision: 3);
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void NormalizeRequestBodyDebugModeOffLeavesSummaryNull()
    {
        AppSettings settings = new();
        settings.DebugMode = false;
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "test-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:8080",
            TemperaturePriority = SamplingPriority.Proxy,
            Temperature = 0.7,
        });

        RequestLog log = new();
        OllamaProxyHandler.NormalizeRequestBody(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            settings,
            log);

        Assert.Null(log.DebugSummary);
    }
}
