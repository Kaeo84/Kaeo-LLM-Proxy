using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies that <see cref="OllamaProxyHandler.RedactSensitiveJsonFields"/> keeps the logged
/// request body identical to what the client actually sent (formatting, key order, and any
/// client-supplied fields such as <c>reasoning_effort</c>), while still redacting only the
/// values of sensitive fields.
/// </summary>
public class RequestBodyRedactionTests
{
    [Fact]
    public void CleanBodyIsReturnedVerbatim()
    {
        // Pretty-printed, with a client-supplied reasoning_effort — the exact shape a client
        // app produces. None of these are sensitive, so the string must be returned unchanged.
        string body = """
            {
              "model": "test-model",
              "messages": [
                { "role": "system", "content": "You are a helpful assistant." },
                { "role": "user", "content": "Hello" }
              ],
              "stream": false,
              "reasoning_effort": "high"
            }
            """;

        string result = OllamaProxyHandler.RedactSensitiveJsonFields(body);

        Assert.Equal(body, result);
    }

    [Fact]
    public void SensitiveFieldsAreRedactedWithSurroundingTextPreserved()
    {
        string body = """
            {
              "model": "test-model",
              "authorization": "Bearer secret-value",
              "nested": {
                "token": "abc123",
                "content": "keep me"
              },
              "stream": false
            }
            """;

        string result = OllamaProxyHandler.RedactSensitiveJsonFields(body);

        // Sensitive values replaced...
        Assert.Contains("\"authorization\": \"[REDACTED]\"", result);
        Assert.Contains("\"token\": \"[REDACTED]\"", result);
        // ...while everything else (including nested non-sensitive fields) stays verbatim.
        Assert.Contains("\"content\": \"keep me\"", result);
        Assert.Contains("\"stream\": false", result);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("abc123", result);
    }

    [Fact]
    public void NestedSensitiveObjectValuesAreRedacted()
    {
        string body = """
            { "headers": { "authorization": "Bearer xyz" } }
            """;

        string result = OllamaProxyHandler.RedactSensitiveJsonFields(body);

        Assert.Contains("\"headers\": { \"authorization\": \"[REDACTED]\" }", result);
        Assert.DoesNotContain("xyz", result);
    }

    [Fact]
    public void InvalidJsonIsReturnedUnchanged()
    {
        const string body = "{ not valid json }";

        string result = OllamaProxyHandler.RedactSensitiveJsonFields(body);

        Assert.Equal(body, result);
    }

    [Fact]
    public void BlankBodyIsReturnedUnchanged()
    {
        Assert.Equal(string.Empty, OllamaProxyHandler.RedactSensitiveJsonFields(string.Empty));
        Assert.Equal("   ", OllamaProxyHandler.RedactSensitiveJsonFields("   "));
    }
}
