using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>Output formats offered by the tool window's export menu.</summary>
    internal enum TranscriptFormat
    {
        Markdown,
        PlainText,
        Html
    }

    /// <summary>
    /// Renders the chat transcript for the clipboard and for file export. Keeping both paths on
    /// this one type means a copied selection and an exported file read the same way.
    /// Tool and status lines are excluded: they are execution noise in the window and would bloat
    /// an exported transcript with full tool arguments.
    /// </summary>
    internal static class TranscriptFormatter
    {
        /// <summary>True for lines that belong to the conversation itself (user or assistant).</summary>
        public static bool IsConversation(ChatLine line)
            => line is not null && (line.Kind == "user" || line.Kind == "assistant");

        /// <summary>Renders the given lines in the requested format for file export.</summary>
        public static string Format(IEnumerable<ChatLine> lines, TranscriptFormat format)
        {
            var conversation = (lines ?? Enumerable.Empty<ChatLine>()).Where(IsConversation).ToList();
            if (conversation.Count == 0)
                return "No messages to export.";

            return format switch
            {
                TranscriptFormat.Markdown => ToMarkdown(conversation),
                TranscriptFormat.Html => ToHtml(conversation),
                _ => ToPlainText(conversation),
            };
        }

        /// <summary>
        /// Renders lines as plain "You:" / "Assistant:" text. Used for clipboard copies, where a
        /// header or metadata would just get in the way of pasting.
        /// </summary>
        public static string ToClipboardText(IEnumerable<ChatLine> lines)
        {
            var sb = new StringBuilder();
            foreach (var line in (lines ?? Enumerable.Empty<ChatLine>()).Where(IsConversation))
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(Label(line)).Append(": ").Append(line.Text.Trim());
            }
            return sb.ToString();
        }

        /// <summary>Suggested file name (without extension) for an export.</summary>
        public static string DefaultFileName()
            => "kaeo-chat-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        private static string Label(ChatLine line)
            => line.Kind == "user" ? "You" : "Assistant";

        private static string ToPlainText(List<ChatLine> lines)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                sb.Append(Label(line)).Append(": ").AppendLine(line.Text.Trim());
                sb.AppendLine();
            }
            if (sb.Length > 0) sb.Length -= Environment.NewLine.Length;
            return sb.ToString();
        }

        private static string ToMarkdown(List<ChatLine> lines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Chat Transcript");
            sb.AppendLine();
            sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("---");
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                sb.AppendLine();
                sb.AppendLine($"### {Label(line)}");
                sb.AppendLine();
                sb.AppendLine(line.Text.Trim());
            }
            return sb.ToString();
        }

        private static string ToHtml(List<ChatLine> lines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\" />");
            sb.AppendLine("<title>Chat Transcript</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', sans-serif; margin: 2rem auto; max-width: 52rem; line-height: 1.5; }");
            sb.AppendLine("h1 { font-size: 1.4rem; }");
            sb.AppendLine(".meta { color: #666; font-size: .85rem; }");
            sb.AppendLine(".msg { margin: 1rem 0; padding: .6rem .8rem; border-left: 3px solid #ccc; background: #fafafa; }");
            sb.AppendLine(".msg.user { border-left-color: #0a7ac2; background: #f2f8fc; }");
            sb.AppendLine(".who { font-weight: 600; margin-bottom: .3rem; }");
            sb.AppendLine(".msg pre { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: Consolas, monospace; font-size: .9rem; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<h1>Chat Transcript</h1>");
            sb.AppendLine($"<p class=\"meta\">Exported {WebUtility.HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}</p>");
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                var css = line.Kind == "user" ? "msg user" : "msg assistant";
                sb.AppendLine($"<div class=\"{css}\">");
                sb.AppendLine($"<div class=\"who\">{WebUtility.HtmlEncode(Label(line))}</div>");
                // Text is escaped and kept preformatted: assistant output is markdown, and this
                // preserves code blocks and indentation without pulling in a markdown renderer.
                sb.AppendLine($"<pre>{WebUtility.HtmlEncode(line.Text.Trim())}</pre>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }
    }
}
