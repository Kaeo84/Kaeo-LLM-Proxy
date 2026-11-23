using System;
using System.Linq;
using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

public class AgentNotesTests
{
    private static readonly DateTime Ts = new DateTime(2026, 3, 4, 15, 30, 0);

    [Fact]
    public void FormatEntry_WithTags_IncludesParenthesizedTags()
    {
        var line = AgentNotes.FormatEntry(Ts, "decision, api", "Use slnx for new solutions");
        Assert.Equal("- [2026-03-04 15:30] (decision, api) Use slnx for new solutions", line);
    }

    [Fact]
    public void FormatEntry_NoTags_OmitsParentheses()
    {
        var line = AgentNotes.FormatEntry(Ts, null, "plain note");
        Assert.Equal("- [2026-03-04 15:30] plain note", line);
    }

    [Fact]
    public void FormatEntry_WhitespaceTags_OmitsParentheses()
    {
        var line = AgentNotes.FormatEntry(Ts, "   ", "plain note");
        Assert.Equal("- [2026-03-04 15:30] plain note", line);
    }

    [Fact]
    public void FormatEntry_CollapsesMultilineTextToSingleLine()
    {
        var line = AgentNotes.FormatEntry(Ts, "compaction", "first line\r\nsecond line\nthird\ttab");
        Assert.Equal("- [2026-03-04 15:30] (compaction) first line second line third tab", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Parse_RoundTripsFormattedEntry()
    {
        var line = AgentNotes.FormatEntry(Ts, "tag1, tag2", "hello world");
        var entries = AgentNotes.Parse(line + "\n");
        var e = Assert.Single(entries);
        Assert.Equal(Ts, e.Timestamp);
        Assert.Equal("tag1, tag2", e.Tags);
        Assert.Equal("hello world", e.Text);
        Assert.Equal(line, e.RawLine);
    }

    [Fact]
    public void Parse_EntryWithoutTags_LeavesTagsNull()
    {
        var entries = AgentNotes.Parse("- [2026-03-04 15:30] no tags here\n");
        var e = Assert.Single(entries);
        Assert.Null(e.Tags);
        Assert.Equal("no tags here", e.Text);
    }

    [Fact]
    public void Parse_SkipsHeadersAndProse()
    {
        var content = "# Agent notes\n\nSome intro prose.\n- [2026-03-04 15:30] (t) real entry\nnot an entry\n";
        var entries = AgentNotes.Parse(content);
        var e = Assert.Single(entries);
        Assert.Equal("real entry", e.Text);
    }

    [Fact]
    public void Parse_TextWithParentheses_IsNotMistakenForTags()
    {
        var entries = AgentNotes.Parse("- [2026-03-04 15:30] decided (x) to do y\n");
        var e = Assert.Single(entries);
        Assert.Null(e.Tags);
        Assert.Equal("decided (x) to do y", e.Text);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(AgentNotes.Parse(null));
        Assert.Empty(AgentNotes.Parse(""));
    }

    [Fact]
    public void TailNewestFirst_ReturnsLastNInReverseOrder()
    {
        var content =
            "- [2026-03-04 10:00] one\n" +
            "- [2026-03-04 11:00] two\n" +
            "- [2026-03-04 12:00] three\n";
        var tail = AgentNotes.TailNewestFirst(content, 2);
        Assert.Equal("- [2026-03-04 12:00] three\n- [2026-03-04 11:00] two", tail);
    }

    [Fact]
    public void TailNewestFirst_MoreThanAvailable_ReturnsAllNewestFirst()
    {
        var content = "- [2026-03-04 10:00] one\n- [2026-03-04 11:00] two\n";
        var tail = AgentNotes.TailNewestFirst(content, 50);
        Assert.Equal("- [2026-03-04 11:00] two\n- [2026-03-04 10:00] one", tail);
    }

    [Fact]
    public void TailNewestFirst_NonPositiveCount_UsesDefault()
    {
        var content = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"- [2026-03-04 10:{i:D2}] n{i}")) + "\n";
        var tail = AgentNotes.TailNewestFirst(content, 0);
        var lines = tail.Split('\n');
        Assert.Equal(AgentNotes.DefaultMaxEntries, lines.Length);
        Assert.Equal("- [2026-03-04 10:15] n15", lines[0]);
    }

    [Fact]
    public void TailNewestFirst_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AgentNotes.TailNewestFirst(null, 10));
        Assert.Equal(string.Empty, AgentNotes.TailNewestFirst("# just a header\n", 10));
    }

    [Fact]
    public void AppendEntry_EmptyFile_YieldsEntryWithNewline()
    {
        var entry = AgentNotes.FormatEntry(Ts, null, "first");
        Assert.Equal(entry + "\n", AgentNotes.AppendEntry(null, entry));
        Assert.Equal(entry + "\n", AgentNotes.AppendEntry("", entry));
    }

    [Fact]
    public void AppendEntry_ExistingWithoutTrailingNewline_AddsSeparator()
    {
        var existing = "- [2026-03-04 10:00] old";
        var entry = "- [2026-03-04 15:30] new";
        Assert.Equal(existing + "\n" + entry + "\n", AgentNotes.AppendEntry(existing, entry));
    }

    [Fact]
    public void AppendEntry_ExistingWithTrailingNewline_NoDoubleNewline()
    {
        var existing = "- [2026-03-04 10:00] old\n";
        var entry = "- [2026-03-04 15:30] new";
        Assert.Equal(existing + entry + "\n", AgentNotes.AppendEntry(existing, entry));
    }

    [Fact]
    public void AppendEntry_PreservesExistingContentVerbatim()
    {
        var existing = "# Notes\r\n\r\n- [2026-03-04 10:00] (t) old\r\n";
        var entry = "- [2026-03-04 15:30] new";
        var result = AgentNotes.AppendEntry(existing, entry);
        Assert.StartsWith(existing, result);
        Assert.EndsWith(entry + "\n", result);
    }
}