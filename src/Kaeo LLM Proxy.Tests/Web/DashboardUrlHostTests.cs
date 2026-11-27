using Kaeo.LlmProxy.Core.Models;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// The dashboard hands its address to a browser, so a wildcard bind must never leak into the URL.
/// "0.0.0.0" and "::" mean "listen everywhere", not "go here"; resolving them to localhost is what
/// keeps the tray menu's Open Web Dashboard link and the Windows dashboard URL clickable.
/// </summary>
public sealed class DashboardUrlHostTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBrowsableHost_MapsWildcardBindsToLocalhost(string? address)
        => Assert.Equal("localhost", NetworkingHelpers.ResolveBrowsableHost(address));

    [Fact]
    public void ResolveBrowsableHost_TreatsNullAsLocalhost()
        => Assert.Equal("localhost", NetworkingHelpers.ResolveBrowsableHost(null));

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.5")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void ResolveBrowsableHost_MapsLoopbackToLocalhost(string address)
        => Assert.Equal("localhost", NetworkingHelpers.ResolveBrowsableHost(address));

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("  10.0.0.7  ", "10.0.0.7")]
    [InlineData("proxy.internal", "proxy.internal")]
    [InlineData("fe80::1", "[fe80::1]")]
    [InlineData("[fe80::1]", "[fe80::1]")]
    public void ResolveBrowsableHost_KeepsAConcreteAddressBrowsable(string address, string expected)
        => Assert.Equal(expected, NetworkingHelpers.ResolveBrowsableHost(address));

    [Theory]
    [InlineData("0.0.0.0", 8081)]
    [InlineData("::", 8081)]
    [InlineData("192.168.1.50", 8081)]
    public void ResolvedHost_AlwaysProducesAWellFormedUrl(string address, int port)
    {
        string url = $"http://{NetworkingHelpers.ResolveBrowsableHost(address)}:{port}";

        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? uri));
        Assert.NotEqual("0.0.0.0", uri!.Host);
    }
}