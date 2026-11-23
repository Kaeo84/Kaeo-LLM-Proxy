using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the cheap post-edit syntax check: clean code reports nothing, broken code reports
/// line-anchored errors, and the error cap is respected.
/// </summary>
public class SyntaxCheckTests
{
    [Fact]
    public void CleanCode_ReportsNoErrors()
    {
        var errors = SyntaxCheck.CheckCSharp("class C { void M() { } }");
        Assert.Empty(errors);
    }

    [Fact]
    public void BrokenCode_ReportsLineAnchoredError()
    {
        var errors = SyntaxCheck.CheckCSharp("class C\n{\n    void M(\n}\n");
        Assert.NotEmpty(errors);
        Assert.StartsWith("(line ", errors[0]);
    }

    [Fact]
    public void ManyErrors_RespectsCap()
    {
        var text = string.Concat(Enumerable.Repeat("}\n", 50));
        var errors = SyntaxCheck.CheckCSharp(text, maxErrors: 3);
        Assert.InRange(errors.Count, 1, 3);
    }

    [Fact]
    public void EmptyText_ReportsNoErrors()
    {
        Assert.Empty(SyntaxCheck.CheckCSharp(string.Empty));
    }
}