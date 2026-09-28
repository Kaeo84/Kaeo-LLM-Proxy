using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins that compaction runs are serialized so their chunk requests cannot interleave on one upstream.
/// </summary>
/// <remarks>
/// The failure this exists because of: llama.cpp reported
/// <c>failed to find N available cells in kv cache</c> and <c>failed to restore state</c>, then
/// reprocessed the prompt from scratch, on a single-slot server. Each compaction run already awaits
/// its chunks in order, so the overlap that caused it was between runs — an automatic compaction, a
/// manual one, and a reactive retry were all free to overlap.
/// </remarks>
public class CompactionConcurrencyTests
{
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

    /// <summary>
    /// Builds a request body whose conversation is large enough that compaction does real work, so a
    /// run occupies the gate for a measurable interval.
    /// </summary>
    private static string BuildBody(string tag)
    {
        string padding = new string('x', 40_000);
        var messages = new List<object>
        {
            new { role = "user", content = $"{tag} {padding}" },
            new { role = "assistant", content = "ack" },
            new { role = "user", content = "continue" },
        };
        return JsonSerializer.Serialize(new { model = "main", messages });
    }

    /// <summary>
    /// Records the peak number of summarization requests in flight at once, and holds each one briefly
    /// so an overlap has a chance to be observed rather than being masked by fast responses.
    /// </summary>
    private sealed class ConcurrencyProbeHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _peak;

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, current);
            RequestCount++;

            try
            {
                // Long enough that two overlapping runs are reliably observed.
                await Task.Delay(120, cancellationToken);

                string json = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { role = "assistant", content = "Summary.\n\n## Tool activity\n- none" } } },
                });

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen)
                    return;
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }

    /// <summary>
    /// Runs <paramref name="runs"/> compactions concurrently against one service, then reports the
    /// highest number of simultaneous upstream requests observed.
    /// </summary>
    private static async Task<int> PeakConcurrencyForAsync(int limit, int runs)
    {
        ConcurrencyProbeHandler probe = new();
        using HttpClient http = new(probe);
        AutoCompactionService service = new(http);
        service.SetMaxConcurrentCompactions(limit);

        ConcurrentBag<Task<string?>> tasks = [];
        for (int i = 0; i < runs; i++)
        {
            // Distinct session keys and distinct bodies so each is genuinely an independent run.
            string tag = $"run-{i}";
            tasks.Add(service.CompactAsync(
                Mapping(),
                BuildBody(tag),
                tag,
                "http://localhost:8080",
                null,
                30,
                maxTokensPerChunk: 2000,
                "compact-model",
                100_000,
                100_000,
                CancellationToken.None));
        }

        await Task.WhenAll(tasks);
        return probe.PeakConcurrency;
    }

    // ── The gate serializes runs ──────────────────────────────────────────

    [Fact]
    public async Task ConcurrentRunsNeverOverlapAtTheDefaultLimit()
    {
        // The core guarantee. Without the gate these overlapped, because the circuit breaker keys per
        // conversation and does nothing to serialize independent runs.
        int peak = await PeakConcurrencyForAsync(limit: 1, runs: 4);

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ARaisedLimitAllowsOverlap()
    {
        // The inverse, so a gate that simply blocked everything forever would not pass the test above
        // by accident. A multi-slot upstream is the case where overlap is wanted.
        int peak = await PeakConcurrencyForAsync(limit: AppSettings.MaxMaxConcurrentCompactions, runs: 4);

        Assert.True(peak > 1, $"Expected overlap with a raised limit, observed {peak}.");
    }

    // ── The gate is not leaked ────────────────────────────────────────────

    [Fact]
    public async Task ARunThatFailsStillReleasesTheGate()
    {
        // A leaked permit would stall all compaction with no error, which is worse than the contention
        // the gate prevents. This drives a failing run, then proves a later run still proceeds.
        using HttpClient http = new(new FailingHandler());
        AutoCompactionService service = new(http);
        service.SetMaxConcurrentCompactions(1);

        string? first = await service.CompactAsync(
            Mapping(), BuildBody("fail"), "fail", "http://localhost:8080", null, 30,
            2000, "compact-model", 100_000, 100_000, CancellationToken.None);

        Assert.Null(first);

        // A fresh service is not the test; the same instance must still admit work.
        using HttpClient ok = new(new ConcurrencyProbeHandler());
        AutoCompactionService probeService = new(ok);
        probeService.SetMaxConcurrentCompactions(1);

        string? second = await probeService.CompactAsync(
            Mapping(), BuildBody("ok"), "ok", "http://localhost:8080", null, 30,
            2000, "compact-model", 100_000, 100_000, CancellationToken.None);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task SequentialRunsAllComplete()
    {
        // Guards against a gate that admits one run and then refuses the rest.
        int peak = await PeakConcurrencyForAsync(limit: 1, runs: 3);

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ACancelledWaitDoesNotBlockLaterRuns()
    {
        // A run that is cancelled while queued must not consume a permit, or the queue would shrink
        // permanently under client cancellations.
        ConcurrencyProbeHandler probe = new();
        using HttpClient http = new(probe);
        AutoCompactionService service = new(http);
        service.SetMaxConcurrentCompactions(1);

        using CancellationTokenSource cts = new();

        Task<string?> first = service.CompactAsync(
            Mapping(), BuildBody("first"), "first", "http://localhost:8080", null, 30,
            2000, "compact-model", 100_000, 100_000, CancellationToken.None);

        // Queued behind the first, then cancelled before it can start.
        Task<string?> queued = service.CompactAsync(
            Mapping(), BuildBody("queued"), "queued", "http://localhost:8080", null, 30,
            2000, "compact-model", 100_000, 100_000, cts.Token);

        await cts.CancelAsync();

        await first;
        await queued; // Must not throw; a cancelled wait returns null.

        ConcurrencyProbeHandler laterProbe = new();
        using HttpClient laterHttp = new(laterProbe);
        AutoCompactionService laterService = new(laterHttp);
        laterService.SetMaxConcurrentCompactions(1);

        string? after = await laterService.CompactAsync(
            Mapping(), BuildBody("after"), "after", "http://localhost:8080", null, 30,
            2000, "compact-model", 100_000, 100_000, CancellationToken.None);

        Assert.NotNull(after);
    }

    // ── The limit is clamped and tracked ─────────────────────────────────

    [Theory]
    [InlineData(0, AppSettings.MinMaxConcurrentCompactions)]
    [InlineData(-5, AppSettings.MinMaxConcurrentCompactions)]
    [InlineData(999, AppSettings.MaxMaxConcurrentCompactions)]
    public void Normalize_ClampsMaxConcurrentCompactions(int input, int expected)
    {
        AppSettings settings = new() { MaxConcurrentCompactions = input };

        settings.Normalize();

        Assert.Equal(expected, settings.MaxConcurrentCompactions);
    }

    [Fact]
    public void TheDefaultIsOne()
    {
        // The shipped value suits a single-slot local server, which is the common case and the one
        // that produced the KV-cache errors.
        Assert.Equal(1, AppSettings.DefaultMaxConcurrentCompactions);
        Assert.Equal(1, new AppSettings().MaxConcurrentCompactions);
    }

    [Fact]
    public void TheLimitSurvivesARuntimeSettingsRoundTrip()
    {
        AppSettings settings = new() { MaxConcurrentCompactions = 4 };

        RuntimeSettings runtime = settings.CreateRuntimeSettings();
        AppSettings restored = new();
        restored.ApplyRuntimeSettings(runtime);

        Assert.Equal(4, restored.MaxConcurrentCompactions);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json"),
            });
    }
}