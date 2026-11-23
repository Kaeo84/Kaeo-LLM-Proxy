using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Kaeo.LlmProxy.VSExtension.Core
{
    /// <summary>
    /// In-process diagnostic sink for the extension. Lines are only recorded while a debugger is
    /// attached (the F5 experimental instance), and a bounded ring buffer is kept so the debug tool
    /// window can replay history when it is opened after the events happened.
    /// </summary>
    internal static class DebugLog
    {
        private const int MaxLines = 4000;
        private static readonly object _gate = new();
        private static readonly Queue<string> _lines = new();
        private static long _seq;

        /// <summary>Raised for every recorded line, possibly from a background thread.</summary>
        public static event Action<string>? LineWritten;

        /// <summary>
        /// Raised for every recorded line with its monotonic sequence number, possibly from a
        /// background thread. Lets a subscriber subscribe before snapshotting and skip lines the
        /// snapshot already covers (sequence &lt;= snapshot sequence) without losing or duplicating any.
        /// </summary>
        public static event Action<long, string>? LineWrittenSequenced;

        /// <summary>True when the plugin is running under a debugger.</summary>
        public static bool IsEnabled => Debugger.IsAttached;

        /// <summary>Records a general informational line.</summary>
        public static void Info(string message) => Write("INFO ", message);

        /// <summary>Records a chatty diagnostic line (state transitions, request summaries).</summary>
        public static void Verbose(string message) => Write("DEBUG", message);

        /// <summary>Records a recoverable problem that did not surface to the user.</summary>
        public static void Warn(string message) => Write("WARN ", message);

        /// <summary>
        /// Records a failure with full detail. The exception is rendered via <c>ToString()</c> so the
        /// type, message, inner chain and stack trace all land in the window.
        /// </summary>
        public static void Error(string message, Exception? ex = null)
            => Write("ERROR", ex is null ? message : message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            // Errors must be recorded even without an attached debugger: they are the only
            // trace of a failed send in normal (non-F5) use. Verbose/Info stay gated.
            if (level != "ERROR" && !IsEnabled)
                return; 

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";

            long seq;
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                    _lines.Dequeue();
                seq = ++_seq;
            }

            // Invoke each subscriber independently: a throwing subscriber (e.g. a debug window
            // doing cross-thread UI work) must not propagate into the logging call site — many
            // call sites are inside catch blocks, and McpServerManager.Observe relies on the
            // invariant that DebugLog never throws. Subscribers must unsubscribe on window close
            // (the static event otherwise roots them for the process lifetime).
            var handlers = LineWritten;
            if (handlers is not null)
            {
                foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
                {
                    try { handler(line); }
                    catch { /* subscriber fault is isolated; never throw from a log call */ }
                }
            }
            var seqHandlers = LineWrittenSequenced;
            if (seqHandlers is not null)
            {
                foreach (Action<long, string> handler in seqHandlers.GetInvocationList().Cast<Action<long, string>>())
                {
                    try { handler(seq, line); }
                    catch { /* subscriber fault is isolated; never throw from a log call */ }
                }
            }
            Debug.WriteLine(line);
        }

        /// <summary>Everything buffered so far, oldest first.</summary>
        public static string Snapshot()
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }

        /// <summary>
        /// Everything buffered so far (oldest first) plus the sequence number of the last buffered
        /// line. A subscriber that registered before calling this can skip events whose sequence is
        /// &lt;= <paramref name="lastSequence"/> because the snapshot already contains them.
        /// </summary>
        public static string Snapshot(out long lastSequence)
        {
            lock (_gate)
            {
                lastSequence = _seq;
                return string.Join(Environment.NewLine, _lines);
            }
        }

        /// <summary>Drops the buffered history (the debug window's own Clear action).</summary>
        public static void Clear()
        {
            lock (_gate)
                _lines.Clear();
        }
    }
}
