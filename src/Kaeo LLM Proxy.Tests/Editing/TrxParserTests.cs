using System.Xml;
using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the pure TRX parser against captured vstest.console result shapes: counters,
/// failure details, FQN mapping from TestDefinitions, skipped tests, and graceful degradation
/// when sections are missing.
/// </summary>
public class TrxParserTests
{
    private const string SampleTrx = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun id=""aaaaaaaa-0000-0000-0000-000000000000"" name=""SampleRun"" xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    <UnitTestResult testId=""11111111-1111-1111-1111-111111111111"" testName=""Ns.Cls.Passing"" outcome=""Passed"" duration=""00:00:00.0100000"" />
    <UnitTestResult testId=""22222222-2222-2222-2222-222222222222"" testName=""Ns.Cls.Failing"" outcome=""Failed"" duration=""00:00:00.0200000"">
      <Output>
        <StdOut>captured console output</StdOut>
        <ErrorInfo>
          <Message>Assert.Equal() Failure: expected 1, actual 2</Message>
          <StackTrace>   at Ns.Cls.Failing() in File.cs:line 10</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testId=""33333333-3333-3333-3333-333333333333"" testName=""Ns.Cls.Skipped"" outcome=""NotExecuted"" />
  </Results>
  <TestDefinitions>
    <UnitTest id=""11111111-1111-1111-1111-111111111111""><TestMethod className=""Ns.Cls"" name=""Passing"" /></UnitTest>
    <UnitTest id=""22222222-2222-2222-2222-222222222222""><TestMethod className=""Ns.Cls"" name=""Failing"" /></UnitTest>
    <UnitTest id=""33333333-3333-3333-3333-333333333333""><TestMethod className=""Ns.Cls"" name=""Skipped"" /></UnitTest>
  </TestDefinitions>
  <ResultSummary outcome=""Failed"">
    <Counters total=""3"" executed=""2"" passed=""1"" failed=""1"" notExecuted=""1"" error=""0"" timeout=""0"" aborted=""0"" inconclusive=""0"" />
  </ResultSummary>
</TestRun>";

    [Fact]
    public void Parse_ReadsSummaryCounters()
    {
        var report = TrxParser.Parse(SampleTrx);
        Assert.Equal("Failed", report.Summary.Outcome);
        Assert.Equal(3, report.Summary.Total);
        Assert.Equal(2, report.Summary.Executed);
        Assert.Equal(1, report.Summary.Passed);
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(1, report.Summary.NotExecuted);
    }

    [Fact]
    public void Parse_FailedTest_HasMessageAndStackTrace()
    {
        var report = TrxParser.Parse(SampleTrx);
        var failed = Assert.Single(report.Results, r => r.Outcome == "Failed");
        Assert.Equal("Assert.Equal() Failure: expected 1, actual 2", failed.ErrorMessage);
        Assert.Contains("File.cs:line 10", failed.StackTrace!);
        Assert.Equal("captured console output", failed.StdOut);
        Assert.Equal("00:00:00.0200000", failed.Duration);
    }

    [Fact]
    public void Parse_MapsFullyQualifiedNameFromDefinitions()
    {
        var report = TrxParser.Parse(SampleTrx);
        Assert.All(report.Results, r => Assert.StartsWith("Ns.Cls.", r.FullyQualifiedName!));
        var passing = Assert.Single(report.Results, r => r.Outcome == "Passed");
        Assert.Equal("Ns.Cls.Passing", passing.FullyQualifiedName);
        Assert.Equal("Ns.Cls.Passing", passing.TestName);
    }

    [Fact]
    public void Parse_NotExecutedTest_HasNoErrorInfo()
    {
        var report = TrxParser.Parse(SampleTrx);
        var skipped = Assert.Single(report.Results, r => r.Outcome == "NotExecuted");
        Assert.Null(skipped.ErrorMessage);
        Assert.Null(skipped.StackTrace);
    }

    [Fact]
    public void Parse_MissingCounters_DerivesFromResults()
    {
        var xml = SampleTrx.Replace(
            @"<Counters total=""3"" executed=""2"" passed=""1"" failed=""1"" notExecuted=""1"" error=""0"" timeout=""0"" aborted=""0"" inconclusive=""0"" />",
            string.Empty);
        var report = TrxParser.Parse(xml);
        Assert.Equal(3, report.Summary.Total);
        Assert.Equal(1, report.Summary.Passed);
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(1, report.Summary.NotExecuted);
        Assert.Equal(2, report.Summary.Executed);
    }

    [Fact]
    public void Parse_MissingDefinitions_FallsBackToTestName()
    {
        var start = SampleTrx.IndexOf("<TestDefinitions>", StringComparison.Ordinal);
        var end = SampleTrx.IndexOf("</TestDefinitions>", StringComparison.Ordinal) + "</TestDefinitions>".Length;
        var xml = SampleTrx.Remove(start, end - start);
        var report = TrxParser.Parse(xml);
        Assert.Equal(3, report.Results.Count);
        Assert.All(report.Results, r => Assert.Null(r.FullyQualifiedName));
        Assert.Equal("Ns.Cls.Passing", report.Results[0].TestName);
    }

    [Fact]
    public void Parse_EmptyResults_YieldsEmptyReport()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results />
  <ResultSummary outcome=""Completed""><Counters total=""0"" executed=""0"" passed=""0"" failed=""0"" /></ResultSummary>
</TestRun>";
        var report = TrxParser.Parse(xml);
        Assert.Empty(report.Results);
        Assert.Equal(0, report.Summary.Total);
        Assert.Equal("Completed", report.Summary.Outcome);
    }

    [Fact]
    public void Parse_InvalidXml_Throws()
    {
        Assert.Throws<XmlException>(() => TrxParser.Parse("<TestRun>"));
    }

    [Fact]
    public void Parse_WrongRoot_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => TrxParser.Parse("<NotATestRun />"));
    }
}