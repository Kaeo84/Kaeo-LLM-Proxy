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

        public DebugOutputControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Replacing (rather than appending) keeps the view in sync with the buffer if the window
            // is closed and reopened, and is a no-op duplicate risk because the buffer holds it all.
            OutputBox.Text = DebugLog.Snapshot();

            if (!DebugLog.IsEnabled && OutputBox.Text.Length == 0)
                OutputBox.Text = "Debug output is captured only while a debugger is attached (F5 / experimental instance).";

            if (!_subscribed)
            {
                DebugLog.LineWritten += OnLineWritten;
                _subscribed = true;
            }

            OutputBox.ScrollToEnd();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
                return;

            DebugLog.LineWritten -= OnLineWritten;
            _subscribed = false;
        }

        // The sink raises on whatever thread logged (the manager uses ConfigureAwait(false)), so the
        // TextBox has to be touched on its own dispatcher. VSTHRD prefers JoinableTaskFactory, but this
        // is a plain WPF control with no package handle - same trade-off as ToolWindowControl.RequestScroll.
#pragma warning disable VSTHRD001, VSTHRD110
        private void OnLineWritten(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Drop the oldest half when the cap is hit; the ring buffer in DebugLog still holds history.
                if (OutputBox.Text.Length > MaxRenderedChars)
                    OutputBox.Text = OutputBox.Text.Substring(MaxRenderedChars / 2);

                if (OutputBox.Text.Length > 0 && OutputBox.Text[OutputBox.Text.Length - 1] != '\n')
                    OutputBox.AppendText(Environment.NewLine);

                OutputBox.AppendText(line);
                OutputBox.ScrollToEnd();
            }));
        }
#pragma warning restore VSTHRD001, VSTHRD110

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DebugLog.Clear();
            OutputBox.Clear();
        }
    }
}
