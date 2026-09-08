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
        private static readonly object _gate = new object();
        private static readonly Queue<string> _lines = new Queue<string>();

        /// <summary>Raised for every recorded line, possibly from a background thread.</summary>
        public static event Action<string>? LineWritten;

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
            if (!IsEnabled)
                return;

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";

            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                    _lines.Dequeue();
            }

            LineWritten?.Invoke(line);
            Debug.WriteLine(line);
        }

        /// <summary>Everything buffered so far, oldest first.</summary>
        public static string Snapshot()
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }

        /// <summary>Drops the buffered history (the debug window's own Clear action).</summary>
        public static void Clear()
        {
            lock (_gate)
                _lines.Clear();
        }
    }
}
