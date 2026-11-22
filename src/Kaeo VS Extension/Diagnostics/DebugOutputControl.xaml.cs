using System;
using Kaeo.LlmProxy.VSExtension.Core;
using System.Windows;
using System.Windows.Controls;

namespace Kaeo.LlmProxy.VSExtension.Diagnostics
{
    /// <summary>
    /// Code-behind for the debug text surface. Replays whatever was already buffered when the window
    /// opens, then appends live lines; writes can arrive from background threads, so every mutation
    /// of the TextBox is marshalled to this control's dispatcher.
    /// </summary>
    public partial class DebugOutputControl : UserControl
    {
        /// <summary>Rough cap on rendered characters so a long debugging session cannot grow the box without bound.</summary>
        private const int MaxRenderedChars = 400_000;

        private bool _subscribed;
        private long _snapshotSeq;
        private int _renderedChars;
        private readonly System.Collections.Concurrent.ConcurrentQueue<(long Seq, string Line)> _pending = new();
        private System.Windows.Threading.DispatcherTimer? _flushTimer;

        public DebugOutputControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // M8: subscribe before snapshotting so no line logged in between is lost. The sequence
            // number lets the flush skip lines the snapshot already covers, so nothing is duplicated.
            if (!_subscribed)
            {
                DebugLog.LineWrittenSequenced += OnLineWrittenSequenced;
                _subscribed = true;
            }

            var snapshot = DebugLog.Snapshot(out _snapshotSeq);
            OutputBox.Text = snapshot;
            _renderedChars = snapshot.Length;

            if (!DebugLog.IsEnabled && _renderedChars == 0)
            {
                OutputBox.Text = "Debug output is captured only while a debugger is attached (F5 / experimental instance).";
                _renderedChars = OutputBox.Text.Length;
            }

            if (_flushTimer is null)
            {
                _flushTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                _flushTimer.Tick += (_, _) => FlushPending();
            }
            if (!_flushTimer.IsEnabled)
                _flushTimer.Start();

            OutputBox.ScrollToEnd();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed)
            {
                DebugLog.LineWrittenSequenced -= OnLineWrittenSequenced;
                _subscribed = false;
            }
            _flushTimer?.Stop();
        }

        // The sink raises on whatever thread logged; just queue the line. A dispatcher timer drains
        // the queue on the UI thread in batches, so the TextBox is never touched off its dispatcher
        // and the whole box text is not re-read on every line.
        private void OnLineWrittenSequenced(long seq, string line) => _pending.Enqueue((seq, line));

        private void FlushPending()
        {
            if (_pending.IsEmpty)
                return;

            var sb = new System.Text.StringBuilder();
            while (_pending.TryDequeue(out var item))
            {
                // Already rendered by the snapshot taken at load time.
                if (item.Seq <= _snapshotSeq)
                    continue;
                if (sb.Length > 0)
                    sb.Append(Environment.NewLine);
                sb.Append(item.Line);
            }
            if (sb.Length == 0)
                return;

            // M8: enforce the cap from a tracked char count; only re-read the box when trimming.
            if (_renderedChars + sb.Length > MaxRenderedChars)
            {
                var text = OutputBox.Text;
                var keep = text.Substring(Math.Max(0, text.Length - MaxRenderedChars / 2));
                OutputBox.Text = keep;
                _renderedChars = keep.Length;
            }

            if (_renderedChars > 0)
            {
                var text = OutputBox.Text;
                if (text.Length == 0 || text[text.Length - 1] != '\n')
                    sb.Insert(0, Environment.NewLine);
            }

            OutputBox.AppendText(sb.ToString());
            _renderedChars += sb.Length;
            OutputBox.ScrollToEnd();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DebugLog.Clear();
            while (_pending.TryDequeue(out _)) { }
            OutputBox.Clear();
            _renderedChars = 0;
        }
    }
}
