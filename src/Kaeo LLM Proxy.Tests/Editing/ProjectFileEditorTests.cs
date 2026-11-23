using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the pure, formatting-preserving project-file editor: SDK-style detection, in-place
/// TFM value replacement, single↔multi element rename, add-when-missing (with and without a
/// PropertyGroup), and exact preservation of indentation/comments/newlines/XML declaration.
/// Samples are newline-normalized so assertions are independent of this file's line endings.
/// </summary>
public class ProjectFileEditorTests
{
    private static string Lf(string s) => s.Replace("\r\n", "\n");
    private static string Crlf(string s) => Lf(s).Replace("\n", "\r\n");

    private const string SdkProject = @"<Project Sdk=""Microsoft.NET.Sdk"">

  <!-- Keep this comment and the blank lines -->
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
  </ItemGroup>

</Project>";

    [Fact]
    public void IsSdkStyle_AttributeForm_True()
    {
        Assert.True(ProjectFileEditor.IsSdkStyle(SdkProject));
    }

    [Fact]
    public void IsSdkStyle_SdkElementForm_True()
    {
        var xml = "<Project>\n  <Sdk Name=\"Microsoft.NET.Sdk\" />\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>";
        Assert.True(ProjectFileEditor.IsSdkStyle(xml));
    }

    [Fact]
    public void IsSdkStyle_ImportSdkForm_True()
    {
        var xml = "<Project>\n  <Import Sdk=\"Microsoft.NET.Sdk\" Project=\"Sdk.props\" />\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>";
        Assert.True(ProjectFileEditor.IsSdkStyle(xml));
    }

    [Fact]
    public void IsSdkStyle_ClassicProject_False()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" />
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>";
        Assert.False(ProjectFileEditor.IsSdkStyle(xml));
    }

    [Fact]
    public void IsSdkStyle_InvalidXml_False()
    {
        Assert.False(ProjectFileEditor.IsSdkStyle("<Project><unclosed>"));
    }

    [Fact]
    public void SetTargetFramework_SingleReplace_PreservesFormattingExactly()
    {
        var input = Crlf(SdkProject);
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        // Only the TFM value changes; every other byte (comments, blank lines, CRLF) is identical.
        Assert.Equal(input.Replace("net8.0", "net10.0"), result.NewXml);
        Assert.Contains("net10.0", result.ChangeDescription);
    }

    [Fact]
    public void SetTargetFramework_MultiReplace_PreservesFormatting()
    {
        var input = Crlf(SdkProject).Replace(
            "<TargetFramework>net8.0</TargetFramework>",
            "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>");
        var result = ProjectFileEditor.SetTargetFramework(input, "net9.0;net10.0");
        Assert.True(result.Success);
        Assert.Equal(input.Replace("net8.0;net9.0", "net9.0;net10.0"), result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_SingleToMulti_RenamesElement()
    {
        var input = Crlf(SdkProject);
        var result = ProjectFileEditor.SetTargetFramework(input, "net8.0;net10.0");
        Assert.True(result.Success);
        var expected = input.Replace(
            "<TargetFramework>net8.0</TargetFramework>",
            "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>");
        Assert.Equal(expected, result.NewXml);
        Assert.Contains("TargetFrameworks", result.ChangeDescription);
    }

    [Fact]
    public void SetTargetFramework_MultiToSingle_RenamesElement()
    {
        var input = Crlf(SdkProject).Replace(
            "<TargetFramework>net8.0</TargetFramework>",
            "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>");
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        var expected = input.Replace(
            "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>",
            "<TargetFramework>net10.0</TargetFramework>");
        Assert.Equal(expected, result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_AddWhenMissing_MatchesChildIndent()
    {
        var input = Crlf(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        var expected = Crlf(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");
        Assert.Equal(expected, result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_NoPropertyGroup_CreatesOne()
    {
        var input = Crlf(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""X"" Version=""1.0"" />
  </ItemGroup>
</Project>");
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        var expected = Crlf(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""X"" Version=""1.0"" />
  </ItemGroup>
</Project>");
        Assert.Equal(expected, result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_LfInput_StaysLf()
    {
        var input = Lf(SdkProject);
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        Assert.DoesNotContain("\r", result.NewXml);
        Assert.Equal(input.Replace("net8.0", "net10.0"), result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_XmlDeclaration_Preserved()
    {
        var input = Crlf("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + SdkProject);
        var result = ProjectFileEditor.SetTargetFramework(input, "net10.0");
        Assert.True(result.Success);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", result.NewXml);
        Assert.Equal(input.Replace("net8.0", "net10.0"), result.NewXml);
    }

    [Fact]
    public void SetTargetFramework_InvalidXml_Fails()
    {
        var result = ProjectFileEditor.SetTargetFramework("<Project><unclosed>", "net10.0");
        Assert.False(result.Success);
        Assert.Contains("not valid XML", result.Error);
    }

    [Fact]
    public void SetTargetFramework_NonProjectRoot_Fails()
    {
        var result = ProjectFileEditor.SetTargetFramework("<Foo />", "net10.0");
        Assert.False(result.Success);
        Assert.Contains("not <Project>", result.Error);
    }

    [Fact]
    public void SetTargetFramework_EmptyFramework_Fails()
    {
        var result = ProjectFileEditor.SetTargetFramework(SdkProject, "  ");
        Assert.False(result.Success);
        Assert.Contains("required", result.Error);
    }
}