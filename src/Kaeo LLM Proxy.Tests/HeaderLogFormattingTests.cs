using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the header-capture formatting rules. Header values are the one place request logging can
/// leak a live credential, so the masking behaviour is asserted directly rather than only through
/// an end-to-end request.
/// </summary>
public class HeaderLogFormattingTests
{
    private static string? Format(params (string Name, string Value)[] headers) =>
        OllamaProxyHandler.FormatHeadersForLog(
            headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)));

    [Fact]
    public void FormatsEachHeaderOnItsOwnLine()
    {
        string? block = Format(("Content-Type", "application/json"), ("Accept", "text/event-stream"));

        Assert.Equal("Content-Type: application/json\nAccept: text/event-stream", block);
    }

    [Fact]
    public void ReturnsNullWhenThereAreNoHeaders()
    {
        Assert.Null(OllamaProxyHandler.FormatHeadersForLog([]));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("Api-Key")]
    [InlineData("X-Goog-Api-Key")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Amz-Security-Token")]
    [InlineData("X-Client-Secret")]
    [InlineData("X-User-Password")]
    public void CredentialHeaderValuesAreMasked(string name)
    {
        string? block = Format((name, "leak-me-if-you-can"));

        Assert.NotNull(block);
        // The name stays so the request remains diagnosable; only the value is replaced.
        Assert.Contains($"{name}:", block);
        Assert.DoesNotContain("leak-me-if-you-can", block);
        Assert.Contains("[REDACTED]", block);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("User-Agent")]
    [InlineData("Cache-Control")]
    [InlineData("X-Request-Id")]
    public void OrdinaryHeaderValuesArePreserved(string name)
    {
        string? block = Format((name, "keep-this-value"));

        Assert.NotNull(block);
        Assert.Contains($"{name}: keep-this-value", block);
    }

    [Fact]
    public void MaskingIsCaseInsensitive()
    {
        string? block = Format(("AUTHORIZATION", "leak-me"));

        Assert.NotNull(block);
        Assert.DoesNotContain("leak-me", block);
    }

    [Fact]
    public void OversizedHeaderSetIsTruncatedAtTheCap()
    {
        // A pathological set (a proxy chain appending forwarding entries) must not write an
        // unbounded blob into every log row.
        List<KeyValuePair<string, string>> headers = [];
        for (int i = 0; i < 4000; i++)
            headers.Add(new KeyValuePair<string, string>($"X-Filler-{i}", new string('x', 100)));

        string? block = OllamaProxyHandler.FormatHeadersForLog(headers);

        Assert.NotNull(block);
        Assert.True(
            block.Length <= RequestLog.MaxHeaderBlockChars + 100,
            $"block length {block.Length} exceeded the cap plus the truncation notice");
        Assert.Contains("truncated", block);
    }

    [Fact]
    public void RedactHeaderBlockScrubsAnAlreadyFormattedBlock()
    {
        // Response headers are read back as name/value pairs, but a caller holding a formatted
        // block must still be able to scrub it.
        string? scrubbed = OllamaProxyHandler.RedactHeaderBlock(
            "Content-Type: application/json\nSet-Cookie: session=abc123\nX-Trace: keep");

        Assert.NotNull(scrubbed);
        Assert.Contains("Set-Cookie: [REDACTED]", scrubbed);
        Assert.DoesNotContain("session=abc123", scrubbed);
        Assert.Contains("X-Trace: keep", scrubbed);
    }

    [Fact]
    public void RedactHeaderBlockLeavesNullAndBlankAlone()
    {
        Assert.Null(OllamaProxyHandler.RedactHeaderBlock(null));
        Assert.Equal(string.Empty, OllamaProxyHandler.RedactHeaderBlock(string.Empty));
    }
}