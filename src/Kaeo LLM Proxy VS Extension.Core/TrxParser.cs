using System.Xml.Linq;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>One test outcome parsed from a TRX (Visual Studio test results) file.</summary>
public sealed class TrxTestResult
{
    public string TestName { get; set; } = string.Empty;
    public string? FullyQualifiedName { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? StdOut { get; set; }
}

/// <summary>Run-level counters from the TRX ResultSummary.</summary>
public sealed class TrxSummary
{
    public string? Outcome { get; set; }
    public int Total { get; set; }
    public int Executed { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int NotExecuted { get; set; }
    public int Error { get; set; }
    public int Timeout { get; set; }
    public int Aborted { get; set; }
    public int Inconclusive { get; set; }
}

public sealed class TrxReport
{
    public TrxSummary Summary { get; set; } = new TrxSummary();
    public List<TrxTestResult> Results { get; set; } = new List<TrxTestResult>();
}

/// <summary>
/// Pure parser for vstest.console TRX output (TeamTest 2010 schema). Maps each UnitTestResult to
/// its fully-qualified name via TestDefinitions, and surfaces failure messages/stack traces as
/// data. When ResultSummary counters are absent they are derived from the individual results.
/// </summary>
public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TrxReport Parse(string trxXml)
    {
        var doc = XDocument.Parse(trxXml);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "TestRun")
            throw new FormatException("Not a TRX document (missing TestRun root element).");

        var fqnById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defs = root.Element(Ns + "TestDefinitions");
        if (defs is not null)
        {
            foreach (var unitTest in defs.Elements(Ns + "UnitTest"))
            {
                var id = unitTest.Attribute("id")?.Value;
                var method = unitTest.Element(Ns + "TestMethod");
                if (id is null || method is null) continue;
                var className = method.Attribute("className")?.Value;
                var name = method.Attribute("name")?.Value;
                var fqn = string.IsNullOrEmpty(className) ? name : className + "." + name;
                if (!string.IsNullOrEmpty(fqn))
                    fqnById[id] = fqn!;
            }
        }

        var report = new TrxReport();
        var results = root.Element(Ns + "Results");
        if (results is not null)
        {
            foreach (var r in results.Elements(Ns + "UnitTestResult"))
            {
                var testId = r.Attribute("testId")?.Value ?? string.Empty;
                fqnById.TryGetValue(testId, out var fqn);
                var output = r.Element(Ns + "Output");
                var errorInfo = output?.Element(Ns + "ErrorInfo");
                report.Results.Add(new TrxTestResult
                {
                    TestName = r.Attribute("testName")?.Value ?? fqn ?? "(unknown)",
                    FullyQualifiedName = fqn,
                    Outcome = r.Attribute("outcome")?.Value ?? "Unknown",
                    Duration = r.Attribute("duration")?.Value,
                    ErrorMessage = errorInfo?.Element(Ns + "Message")?.Value,
                    StackTrace = errorInfo?.Element(Ns + "StackTrace")?.Value,
                    StdOut = output?.Element(Ns + "StdOut")?.Value,
                });
            }
        }

        var summary = root.Element(Ns + "ResultSummary");
        if (summary is not null)
        {
            report.Summary.Outcome = summary.Attribute("outcome")?.Value;
            var counters = summary.Element(Ns + "Counters");
            if (counters is not null)
            {
                report.Summary.Total = AttrInt(counters, "total");
                report.Summary.Executed = AttrInt(counters, "executed");
                report.Summary.Passed = AttrInt(counters, "passed");
                report.Summary.Failed = AttrInt(counters, "failed");
                report.Summary.NotExecuted = AttrInt(counters, "notExecuted");
                report.Summary.Error = AttrInt(counters, "error");
                report.Summary.Timeout = AttrInt(counters, "timeout");
                report.Summary.Aborted = AttrInt(counters, "aborted");
                report.Summary.Inconclusive = AttrInt(counters, "inconclusive");
            }
        }

        if (report.Summary.Total == 0 && report.Results.Count > 0)
        {
            report.Summary.Total = report.Results.Count;
            report.Summary.Passed = report.Results.Count(r => string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase));
            report.Summary.Failed = report.Results.Count(r => string.Equals(r.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
            report.Summary.NotExecuted = report.Results.Count(r => string.Equals(r.Outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase));
            report.Summary.Executed = report.Summary.Total - report.Summary.NotExecuted;
        }

        return report;
    }

    private static int AttrInt(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, out var n) ? n : 0;
    }
}