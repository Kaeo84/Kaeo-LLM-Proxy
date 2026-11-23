using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the pure line-edit engine: range semantics, line-ending/trailing-newline
/// preservation, staleness guards, and all-or-nothing batch validation with bottom-up apply.
/// </summary>
public class LineEditorTests
{
    private static LineEdit Edit(int start, int end, string mode = "replace", string? content = null,
        string? expectFirst = null, string? expectLast = null) => new()
    {
        StartLine = start,
        EndLine = end,
        Mode = mode,
        NewContent = content,
        ExpectedFirstLine = expectFirst,
        ExpectedLastLine = expectLast,
    };

    [Fact]
    public void Replace_SingleLine_PreservesCrlf()
    {
        var result = LineEditor.Apply("a\r\nb\r\nc\r\n", new[] { Edit(2, 2, content: "B") });
        Assert.True(result.Success);
        Assert.Equal("a\r\nB\r\nc\r\n", result.NewText);
        Assert.Equal(3, result.NewLineCount);
    }

    [Fact]
    public void Replace_Range_WithMoreLines()
    {
        var result = LineEditor.Apply("a\nb\nc\nd\n", new[] { Edit(2, 3, content: "x\ny\nz") });
        Assert.True(result.Success);
        Assert.Equal("a\nx\ny\nz\nd\n", result.NewText);
        Assert.Equal(5, result.NewLineCount);
    }

    [Fact]
    public void Replace_EmptyContent_RemovesRange()
    {
        var result = LineEditor.Apply("a\nb\nc\n", new[] { Edit(2, 2, content: "") });
        Assert.True(result.Success);
        Assert.Equal("a\nc\n", result.NewText);
    }

    [Fact]
    public void Delete_Range()
    {
        var result = LineEditor.Apply("a\nb\nc\nd\n", new[] { Edit(2, 3, mode: "delete") });
        Assert.True(result.Success);
        Assert.Equal("a\nd\n", result.NewText);
    }

    [Fact]
    public void InsertBefore_FirstLine()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(1, 1, mode: "insertBefore", content: "head") });
        Assert.True(result.Success);
        Assert.Equal("head\na\nb\n", result.NewText);
    }

    [Fact]
    public void InsertAfter_LastLine()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 2, mode: "insertAfter", content: "tail") });
        Assert.True(result.Success);
        Assert.Equal("a\nb\ntail\n", result.NewText);
    }

    [Fact]
    public void LfFile_StaysLf_EvenWithCrlfContent()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 2, content: "x\r\ny") });
        Assert.True(result.Success);
        Assert.Equal("a\nx\ny\n", result.NewText);
    }

    [Fact]
    public void CrlfFile_NormalizesLfContent()
    {
        var result = LineEditor.Apply("a\r\nb\r\n", new[] { Edit(2, 2, content: "x\ny") });
        Assert.True(result.Success);
        Assert.Equal("a\r\nx\r\ny\r\n", result.NewText);
    }

    [Fact]
    public void NoTrailingNewline_StaysWithout()
    {
        var result = LineEditor.Apply("a\nb", new[] { Edit(2, 2, content: "B") });
        Assert.True(result.Success);
        Assert.Equal("a\nB", result.NewText);
    }

    [Fact]
    public void TrailingNewline_IsKept()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 2, content: "B") });
        Assert.True(result.Success);
        Assert.Equal("a\nB\n", result.NewText);
    }

    [Fact]
    public void MixedEndings_DominantWins()
    {
        // Two CRLF vs one LF: CRLF dominates and the stray LF line is normalized on rejoin.
        var result = LineEditor.Apply("a\r\nb\r\nc\n", new[] { Edit(1, 1, content: "A") });
        Assert.True(result.Success);
        Assert.Equal("A\r\nb\r\nc\r\n", result.NewText);
    }

    [Fact]
    public void NewContent_TrailingNewlineIgnored()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 2, content: "x\ny\n") });
        Assert.True(result.Success);
        Assert.Equal("a\nx\ny\n", result.NewText);
        Assert.Equal(3, result.NewLineCount);
    }

    [Fact]
    public void StaleExpectedFirstLine_Rejected_WithActualContent()
    {
        var result = LineEditor.Apply("a\nactual\n", new[] { Edit(2, 2, content: "x", expectFirst: "expected") });
        Assert.False(result.Success);
        Assert.Contains("actual", result.Error!);
        Assert.Contains("expected", result.Error!);
        Assert.Null(result.NewText);
    }

    [Fact]
    public void StaleExpectedLastLine_Rejected()
    {
        var result = LineEditor.Apply("a\nb\nc\n", new[] { Edit(1, 3, content: "x", expectLast: "zzz") });
        Assert.False(result.Success);
        Assert.Contains("line 3", result.Error!);
    }

    [Fact]
    public void Guards_CompareTrimmed()
    {
        var result = LineEditor.Apply("a\n    indented  \nc\n",
            new[] { Edit(2, 2, content: "x", expectFirst: "indented", expectLast: "indented") });
        Assert.True(result.Success);
    }

    [Fact]
    public void InsertAfter_GuardAnchorsEndLine()
    {
        var result = LineEditor.Apply("a\nb\nc\n",
            new[] { Edit(1, 2, mode: "insertAfter", content: "x", expectFirst: "b", expectLast: "b") });
        Assert.True(result.Success);
        Assert.Equal("a\nb\nx\nc\n", result.NewText);
    }

    [Fact]
    public void InsertBefore_GuardAnchorsStartLine()
    {
        var result = LineEditor.Apply("a\nb\nc\n",
            new[] { Edit(2, 3, mode: "insertBefore", content: "x", expectFirst: "b", expectLast: "b") });
        Assert.True(result.Success);
        Assert.Equal("a\nx\nb\nc\n", result.NewText);
    }

    [Fact]
    public void RangePastEof_Rejected()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 5, content: "x") });
        Assert.False(result.Success);
        Assert.Contains("past end of file", result.Error!);
    }

    [Fact]
    public void ZeroStartLine_Rejected()
    {
        var result = LineEditor.Apply("a\n", new[] { Edit(0, 1, content: "x") });
        Assert.False(result.Success);
    }

    [Fact]
    public void EndBeforeStart_Rejected()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(2, 1, content: "x") });
        Assert.False(result.Success);
    }

    [Fact]
    public void UnknownMode_Rejected()
    {
        var result = LineEditor.Apply("a\n", new[] { Edit(1, 1, mode: "splice", content: "x") });
        Assert.False(result.Success);
        Assert.Contains("Unknown edit mode", result.Error!);
    }

    [Fact]
    public void ReplaceWithoutContent_Rejected()
    {
        var result = LineEditor.Apply("a\n", new[] { Edit(1, 1) });
        Assert.False(result.Success);
        Assert.Contains("newContent", result.Error!);
    }

    [Fact]
    public void EmptyEditList_Rejected()
    {
        var result = LineEditor.Apply("a\n", System.Array.Empty<LineEdit>());
        Assert.False(result.Success);
    }

    [Fact]
    public void Batch_AppliesBottomUp_OriginalNumbering()
    {
        var result = LineEditor.Apply("a\nb\nc\nd\ne\n", new[]
        {
            Edit(1, 1, content: "A"),
            Edit(4, 5, content: "D\nE\nF"),
        });
        Assert.True(result.Success);
        Assert.Equal("A\nb\nc\nD\nE\nF\n", result.NewText);
    }

    [Fact]
    public void Batch_OverlapRejected_NothingApplied()
    {
        var result = LineEditor.Apply("a\nb\nc\nd\n", new[]
        {
            Edit(1, 2, content: "x"),
            Edit(2, 3, content: "y"),
        });
        Assert.False(result.Success);
        Assert.Contains("overlap", result.Error!);
        Assert.Null(result.NewText);
    }

    [Fact]
    public void Batch_DuplicateInsertPosition_Rejected()
    {
        var result = LineEditor.Apply("a\nb\n", new[]
        {
            Edit(2, 2, mode: "insertBefore", content: "x"),
            Edit(2, 2, mode: "insertBefore", content: "y"),
        });
        Assert.False(result.Success);
        Assert.Contains("same position", result.Error!);
    }

    [Fact]
    public void Batch_InsertAfterOne_EqualsInsertBeforeNext_Rejected()
    {
        // insertAfter line 1 and insertBefore line 2 target the same gap.
        var result = LineEditor.Apply("a\nb\n", new[]
        {
            Edit(1, 1, mode: "insertAfter", content: "x"),
            Edit(2, 2, mode: "insertBefore", content: "y"),
        });
        Assert.False(result.Success);
    }

    [Fact]
    public void Batch_StaleSecondEdit_RejectsWholeBatch()
    {
        var result = LineEditor.Apply("a\nb\nc\n", new[]
        {
            Edit(1, 1, content: "A"),
            Edit(3, 3, content: "C", expectFirst: "wrong"),
        });
        Assert.False(result.Success);
        Assert.Null(result.NewText);
    }

    [Fact]
    public void Batch_AdjacentNonOverlappingEdits_Ok()
    {
        var result = LineEditor.Apply("a\nb\nc\nd\n", new[]
        {
            Edit(1, 2, content: "X"),
            Edit(3, 4, content: "Y"),
        });
        Assert.True(result.Success);
        Assert.Equal("X\nY\n", result.NewText);
    }

    [Fact]
    public void Preview_HasNumberedLinesAroundTopEdit()
    {
        var result = LineEditor.Apply("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", new[] { Edit(5, 5, content: "five") });
        Assert.True(result.Success);
        Assert.Contains("5: five", result.Preview!);
        Assert.Contains("2: 2", result.Preview!);
    }

    [Fact]
    public void EmptyFile_TreatedAsOneEmptyLine_InsertWorks()
    {
        var result = LineEditor.Apply("", new[] { Edit(1, 1, mode: "insertBefore", content: "first") });
        Assert.True(result.Success);
        // An empty file is one empty line; with no newline evidence the engine defaults to CRLF,
        // and the original (empty) last line keeps its no-trailing-newline state.
        Assert.Equal("first\r\n", result.NewText);
        Assert.Equal(2, result.NewLineCount);
    }

    [Fact]
    public void DeleteAllLines_YieldsEmptyText()
    {
        var result = LineEditor.Apply("a\nb\n", new[] { Edit(1, 2, mode: "delete") });
        Assert.True(result.Success);
        Assert.Equal("", result.NewText);
        Assert.Equal(0, result.NewLineCount);
    }
}