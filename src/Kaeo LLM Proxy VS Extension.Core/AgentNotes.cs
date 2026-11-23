using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>One parsed line of the agent notes file (<c>.kaeo/notes.md</c>).</summary>
public sealed class NoteEntry
{
    public NoteEntry(DateTime? timestamp, string? tags, string text, string rawLine)
    {
        Timestamp = timestamp;
        Tags = tags;
        Text = text;
        RawLine = rawLine;
    }

    public DateTime? Timestamp { get; }
    public string? Tags { get; }
    public string Text { get; }
    public string RawLine { get; }
}

/// <summary>
/// Pure formatting/parsing for the agent task-memory convention. Entries are single lines
/// <c>- [yyyy-MM-dd HH:mm] (tags) text</c> appended chronologically; readers take a newest-first
/// tail. File IO (creation, sharing-violation tolerance) lives in the extension layer.
/// </summary>
public static class AgentNotes
{
    public const int DefaultMaxEntries = 10;

    private static readonly Regex EntryRegex = new Regex(
        @"^- \[(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\](?: \(([^)]*)\))?(?: (.*))?$",
        RegexOptions.Compiled);

    /// <summary>Formats one entry line. Multi-line text/tags collapse to a single line so the
    /// file stays line-parseable; the tags segment is omitted entirely when empty.</summary>
    public static string FormatEntry(DateTime timestamp, string? tags, string text)
    {
        var clean = Collapse(text);
        var tagPart = string.IsNullOrWhiteSpace(tags) ? string.Empty : " (" + Collapse(tags) + ")";
        return "- [" + timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "]" + tagPart + " " + clean;
    }

    /// <summary>Parses every well-formed entry line in file (chronological) order; non-entry
    /// lines (headers, blanks, prose) are skipped.</summary>
    public static IReadOnlyList<NoteEntry> Parse(string? content)
    {
        var list = new List<NoteEntry>();
        if (string.IsNullOrEmpty(content))
            return list;

        foreach (var raw in content!.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = EntryRegex.Match(line);
            if (!m.Success)
                continue;

            DateTime? ts = DateTime.TryParseExact(
                m.Groups[1].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt) ? dt : (DateTime?)null;
            var tags = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null;
            var text = m.Groups[3].Success ? m.Groups[3].Value : string.Empty;
            list.Add(new NoteEntry(ts, tags, text, line));
        }

        return list;
    }

    /// <summary>Returns the last <paramref name="maxEntries"/> entries, newest first, as raw
    /// lines joined by '\n'. Non-positive counts fall back to <see cref="DefaultMaxEntries"/>.</summary>
    public static string TailNewestFirst(string? content, int maxEntries)
    {
        if (maxEntries < 1)
            maxEntries = DefaultMaxEntries;

        var entries = Parse(content);
        if (entries.Count == 0)
            return string.Empty;

        var newestFirst = entries.Skip(Math.Max(0, entries.Count - maxEntries)).Reverse();
        return string.Join("\n", newestFirst.Select(e => e.RawLine));
    }

    /// <summary>Appends an entry to existing content, preserving the existing bytes verbatim and
    /// guaranteeing exactly one trailing newline. An empty/null file yields just the entry.</summary>
    public static string AppendEntry(string? existingContent, string entry)
    {
        if (string.IsNullOrEmpty(existingContent))
            return entry + "\n";

        var separator = existingContent!.EndsWith("\n") ? string.Empty : "\n";
        return existingContent + separator + entry + "\n";
    }

    private static string Collapse(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        var cleaned = s!.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");
        return cleaned.Trim();
    }
}