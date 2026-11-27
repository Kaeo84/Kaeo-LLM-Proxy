using Kaeo.LlmProxy.Core.Models;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Logging;

/// <summary>
/// The Requests views label traffic by client, derived from the stored User-Agent. These cases pin
/// the classification rules that make extension traffic separable from Copilot's.
/// </summary>
public sealed class ClientTagTests
{
    [Theory]
    [InlineData("Kaeo-VSExt/1.2.3.4", ClientTag.VisualStudioExtension)]
    [InlineData("kaeo-vsext/0.1", ClientTag.VisualStudioExtension)]
    [InlineData("GitHubCopilotChat/0.26.7", ClientTag.Copilot)]
    [InlineData("copilot-language-server/1.0", ClientTag.Copilot)]
    [InlineData("ollama-python/0.4.2", "Ollama")]
    [InlineData("OpenAI/Python 1.51.0", "OpenAI SDK")]
    [InlineData("python-requests/2.32.3", "Python")]
    [InlineData("curl/8.9.1", "curl")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Browser")]
    public void Classify_RecognisesKnownClients(string userAgent, string expected)
        => Assert.Equal(expected, ClientTag.Classify(userAgent));

    [Fact]
    public void Classify_PrefersTheExtensionToken_WhenAnotherClientIsAlsoNamed()
    {
        // The extension's own label must survive a UA that mentions Copilot, otherwise extension
        // traffic would be misfiled as Copilot's and the filter would be useless.
        Assert.Equal(ClientTag.VisualStudioExtension, ClientTag.Classify("Kaeo-VSExt/1.0 GitHubCopilot"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomeUnknownTool/9.9")]
    public void Classify_ReturnsNull_WhenNothingIsRecognised(string? userAgent)
        => Assert.Null(ClientTag.Classify(userAgent));

    [Fact]
    public void VisualStudioExtension_LabelMatchesTheDashboardFilterOption()
    {
        // app.js offers "VS extension" as a filter value; renaming the constant without renaming
        // the option would silently filter nothing.
        Assert.Equal("VS extension", ClientTag.VisualStudioExtension);
    }
}