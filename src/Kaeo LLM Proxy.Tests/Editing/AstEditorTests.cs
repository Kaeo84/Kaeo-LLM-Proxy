using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies AST-aware editing: member replacement is syntax-validated, trivia and line
/// endings are preserved, ambiguous/missing/invalid targets are refused without changes,
/// and using directives are added idempotently.
/// </summary>
public class AstEditorTests
{
    private const string Sample =
        "using System;\n" +
        "\n" +
        "namespace Demo;\n" +
        "\n" +
        "public class Calculator\n" +
        "{\n" +
        "    // Adds two numbers.\n" +
        "    public int Add(int a, int b)\n" +
        "    {\n" +
        "        return a + b;\n" +
        "    }\n" +
        "}\n";

    private const string NewAdd =
        "public int Add(int a, int b)\n" +
        "{\n" +
        "    return a + b + 1;\n" +
        "}";

    [Fact]
    public void ReplaceMethod_ReplacesBody_PreservesComment()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", NewAdd);
        Assert.True(result.Success, result.Error);
        Assert.Contains("return a + b + 1;", result.NewText);
        Assert.Contains("// Adds two numbers.", result.NewText);
        Assert.DoesNotContain("return a + b;", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_LfSource_StaysLf()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", NewAdd);
        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("\r\n", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_CrlfSource_StaysCrlf()
    {
        var crlf = Sample.Replace("\n", "\r\n");
        var result = AstEditor.ReplaceMethod(crlf, "Calculator", "Add", NewAdd);
        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("\n", result.NewText!.Replace("\r\n", ""));
    }

    [Fact]
    public void ReplaceMethod_Overloads_WithoutParameterTypes_Refuses()
    {
        var source =
            "class C\n{\n" +
            "    public int Add(int a, int b) { return a + b; }\n" +
            "    public string Add(string a, string b) { return a + b; }\n}\n";
        var result = AstEditor.ReplaceMethod(source, "C", "Add", "public int Add(int a, int b) { return 0; }");
        Assert.False(result.Success);
        Assert.Contains("overloads", result.Error);
    }

    [Fact]
    public void ReplaceMethod_Overloads_ParameterTypesPickCorrect()
    {
        var source =
            "class C\n{\n" +
            "    public int Add(int a, int b) { return a + b; }\n" +
            "    public string Add(string a, string b) { return a + b; }\n}\n";
        var result = AstEditor.ReplaceMethod(source, "C", "Add",
            "public string Add(string a, string b) { return \"changed\"; }", new[] { "string", "string" });
        Assert.True(result.Success, result.Error);
        Assert.Contains("\"changed\"", result.NewText);
        Assert.Contains("public int Add(int a, int b) { return a + b; }", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_TypeNotFound_Refuses()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Missing", "Add", NewAdd);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void ReplaceMethod_MethodNotFound_Refuses()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Subtract", NewAdd);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void ReplaceMethod_InvalidNewCode_Refuses()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", "public int Add(int a, int b) { return a + ; }");
        Assert.False(result.Success);
        Assert.Contains("does not parse", result.Error);
    }

    [Fact]
    public void ReplaceMethod_WrongKind_Refuses()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", "public int Value { get; set; }");
        Assert.False(result.Success);
        Assert.Contains("must be a method", result.Error);
    }

    [Fact]
    public void ReplaceMethod_NamespaceQualifiedType_Works()
    {
        var result = AstEditor.ReplaceMethod(Sample, "Demo.Calculator", "Add", NewAdd);
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void ReplaceMethod_NestedType_Works()
    {
        var source = "namespace Demo;\n\nclass Outer\n{\n    public class Inner\n    {\n        public void M() { }\n    }\n}\n";
        var result = AstEditor.ReplaceMethod(source, "Outer.Inner", "M", "public void M() { /* new */ }");
        Assert.True(result.Success, result.Error);
        Assert.Contains("/* new */", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_AmbiguousType_Refuses()
    {
        var source = "namespace A { class Dup { public void M() { } } }\nnamespace B { class Dup { public void M() { } } }\n";
        var result = AstEditor.ReplaceMethod(source, "Dup", "M", "public void M() { }");
        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.Error);
    }

    [Fact]
    public void ReplaceMethod_SourceWithSyntaxErrors_Refuses()
    {
        var result = AstEditor.ReplaceMethod("class C { void M( { }", "C", "M", "public void M() { }");
        Assert.False(result.Success);
        Assert.Contains("does not parse cleanly", result.Error);
    }

    [Fact]
    public void ReplaceMethod_IdenticalReplacement_ReportsNoChange()
    {
        var identical = "public int Add(int a, int b)\n    {\n        return a + b;\n    }";
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", identical);
        Assert.False(result.Success);
        Assert.Contains("identical", result.Error);
    }

    [Fact]
    public void ReplaceMethod_NewDocComments_AreKept()
    {
        var newCode = "/// <summary>New docs.</summary>\n" + NewAdd;
        var result = AstEditor.ReplaceMethod(Sample, "Calculator", "Add", newCode);
        Assert.True(result.Success, result.Error);
        Assert.Contains("New docs.", result.NewText);
        Assert.Contains("// Adds two numbers.", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_TrailingComment_IsPreserved()
    {
        var source = Sample.Replace("    }\n}\n", "    } // end\n}\n");
        var result = AstEditor.ReplaceMethod(source, "Calculator", "Add", NewAdd);
        Assert.True(result.Success, result.Error);
        Assert.Contains("// end", result.NewText);
    }

    [Fact]
    public void ReplaceMethod_GenericType_MatchesByName()
    {
        var source = "public class Box<T>\n{\n    public T Get()\n    {\n        return default;\n    }\n}\n";
        var result = AstEditor.ReplaceMethod(source, "Box", "Get", "public T Get() { return default!; }");
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void ReplaceMethod_Record_Works()
    {
        var source = "public record Point(int X, int Y)\n{\n    public int Sum()\n    {\n        return X + Y;\n    }\n}\n";
        var result = AstEditor.ReplaceMethod(source, "Point", "Sum", "public int Sum() { return X * Y; }");
        Assert.True(result.Success, result.Error);
        Assert.Contains("X * Y", result.NewText);
    }

    [Fact]
    public void ReplaceMember_Property_Works()
    {
        var source = "class C\n{\n    public int Value { get; set; }\n}\n";
        var result = AstEditor.ReplaceMember(source, "C", "Value", "public int Value { get; set; } = 5;");
        Assert.True(result.Success, result.Error);
        Assert.Contains("= 5;", result.NewText);
    }

    [Fact]
    public void ReplaceMember_Field_Works()
    {
        var source = "class C\n{\n    private int _count;\n}\n";
        var result = AstEditor.ReplaceMember(source, "C", "_count", "private int _count = 1;");
        Assert.True(result.Success, result.Error);
        Assert.Contains("_count = 1;", result.NewText);
    }

    [Fact]
    public void ReplaceMember_MultiVariableField_Refuses()
    {
        var source = "class C\n{\n    private int _a, _b;\n}\n";
        var result = AstEditor.ReplaceMember(source, "C", "_a", "private int _a = 1;");
        Assert.False(result.Success);
        Assert.Contains("shares a declaration", result.Error);
    }

    [Fact]
    public void ReplaceMember_MethodKind_Refuses()
    {
        var source = "class C\n{\n    public int Value { get; set; }\n}\n";
        var result = AstEditor.ReplaceMember(source, "C", "Value", "public void M() { }");
        Assert.False(result.Success);
        Assert.Contains("must be a property, field, or event", result.Error);
    }

    [Fact]
    public void AddUsing_AddsWhenMissing()
    {
        var result = AstEditor.AddUsing(Sample, "System.Text");
        Assert.True(result.Success, result.Error);
        Assert.Contains("using System.Text;", result.NewText);
        Assert.Contains("using System;", result.NewText);
    }

    [Fact]
    public void AddUsing_AlreadyPresent_Refuses()
    {
        var result = AstEditor.AddUsing(Sample, "System");
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public void AddUsing_NoUsings_FileScopedNamespace_StaysValid()
    {
        var source = "namespace Demo;\n\nclass C { }\n";
        var result = AstEditor.AddUsing(source, "System.Linq");
        Assert.True(result.Success, result.Error);
        Assert.Contains("using System.Linq;", result.NewText);
        Assert.True(result.NewText!.IndexOf("using System.Linq;", StringComparison.Ordinal) <
                    result.NewText.IndexOf("namespace Demo;", StringComparison.Ordinal));
    }

    [Fact]
    public void AddUsing_InvalidNamespace_Refuses()
    {
        var result = AstEditor.AddUsing(Sample, "System..Text");
        Assert.False(result.Success);
        Assert.Contains("not a valid namespace", result.Error);
    }
}