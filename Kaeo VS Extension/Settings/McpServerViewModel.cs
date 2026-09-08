using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// An MCP server row in the settings window's MCP tab. Holds the connection details for one
    /// transport plus the tools pulled from it; <see cref="Tools"/> drives the per-tool enable
    /// checkboxes that decide which tools the model is offered.
    /// </summary>
    public sealed class McpServerViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _transport = "http";
        private string _url = string.Empty;
        private string _apiKey = string.Empty;
        private string _command = string.Empty;
        private string _arguments = string.Empty;
        private bool _enabled = true;
        private bool _isRefreshing;
        private string? _lastError;
        private string? _lastErrorDetail;
        private string? _statusMessage;
        private DateTime? _lastSyncUtc;

        public McpServerViewModel()
        {
            Tools.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ToolSummary));
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        /// <summary>Transport key: "http" (streamable HTTP) or "stdio".</summary>
        public string Transport
        {
            get => _transport;
            set
            {
                _transport = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHttp));
                OnPropertyChanged(nameof(IsStdio));
            }
        }

        /// <summary>Endpoint of a streamable-HTTP server.</summary>
        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        /// <summary>Optional bearer token sent as the Authorization header.</summary>
        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value; OnPropertyChanged(); }
        }

        /// <summary>Executable to launch for a stdio server.</summary>
        public string Command
        {
            get => _command;
            set { _command = value; OnPropertyChanged(); }
        }

        /// <summary>Command-line arguments for a stdio server, space-separated (quote tokens containing spaces).</summary>
        public string Arguments
        {
            get => _arguments;
            set { _arguments = value; OnPropertyChanged(); }
        }

        /// <summary>Disabled servers contribute no tools and are never connected to.</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            set
            {
                _isRefreshing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRefresh));
            }
        }

        /// <summary>Short failure text shown inline, or null when the last attempt succeeded.</summary>
        public string? LastError
        {
            get => _lastError;
            set
            {
                _lastError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(LastErrorTooltip));
            }
        }

        /// <summary>
        /// Full failure text (type, message, inner chain and stack trace) for the tooltip and for
        /// copying, so a truncated inline message can still be investigated.
        /// </summary>
        public string? LastErrorDetail
        {
            get => _lastErrorDetail;
            set
            {
                _lastErrorDetail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastErrorTooltip));
            }
        }

        /// <summary>Tooltip shown when hovering the inline error; also tells the user it is clickable.</summary>
        public string? LastErrorTooltip
            => string.IsNullOrEmpty(_lastErrorDetail)
                ? null
                : _lastErrorDetail + Environment.NewLine + Environment.NewLine + "Click to copy this error.";

        /// <summary>Non-error feedback (e.g. a successful connectivity test), cleared on the next action.</summary>
        public string? StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

        public bool HasError => !string.IsNullOrEmpty(_lastError);

        /// <summary>
        /// Records a failure: the inline line carries the deepest message (usually the readable one),
        /// while the detail keeps the whole <see cref="Exception.ToString"/> including the stack trace.
        /// </summary>
        public void SetError(Exception ex)
        {
            StatusMessage = null;
            LastErrorDetail = ex.ToString();
            LastError = ex.GetBaseException().Message;
        }

        /// <summary>Shows a transient info line and clears any error state.</summary>
        public void SetStatus(string message)
        {
            LastError = null;
            LastErrorDetail = null;
            StatusMessage = message;
        }

        /// <summary>Clears the previous error and status lines before a new action.</summary>
        public void ClearDiagnostics()
        {
            LastError = null;
            LastErrorDetail = null;
            StatusMessage = null;
        }

        /// <summary>True when a tool pull is not in flight (drives the Refresh button).</summary>
        public bool CanRefresh => !IsRefreshing;

        public bool IsHttp => string.Equals(_transport, "http", StringComparison.OrdinalIgnoreCase);

        public bool IsStdio => !IsHttp;

        /// <summary>Choices for the transport dropdown.</summary>
        public IReadOnlyList<string> TransportOptions { get; } = new[] { "http", "stdio" };

        /// <summary>When tools were last pulled successfully, for the status line.</summary>
        public DateTime? LastSyncUtc
        {
            get => _lastSyncUtc;
            set { _lastSyncUtc = value; OnPropertyChanged(nameof(ToolSummary)); }
        }

        /// <summary>Tools reported by the server; the enable flags decide what the model may call.</summary>
        public ObservableCollection<McpToolViewModel> Tools { get; } = new();

        /// <summary>One-line status shown under the tool list.</summary>
        public string ToolSummary
        {
            get
            {
                var enabled = Tools.Count(t => t.Enabled);
                var baseText = Tools.Count == 0
                    ? "No tools pulled yet."
                    : $"{enabled} of {Tools.Count} tools enabled.";
                return _lastSyncUtc is null
                    ? baseText
                    : $"{baseText} Last sync {_lastSyncUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}.";
            }
        }

        /// <summary>
        /// Re-raises <see cref="ToolSummary"/> so the "X of Y tools enabled" count repaints after a
        /// single checkbox toggle, which changes no collection and so never raises it by itself.
        /// </summary>
        public void RefreshToolSummary() => OnPropertyChanged(nameof(ToolSummary));

        /// <summary>
        /// Replaces the tool list from a pull, keeping the current enable flags by tool name.
        /// Internal because <see cref="McpTool"/> is an internal Core type.
        /// </summary>
        internal void ReplaceTools(IEnumerable<McpTool> tools)
        {
            var previous = Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            Tools.Clear();
            foreach (var tool in tools ?? Enumerable.Empty<McpTool>())
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                    continue;

                Tools.Add(new McpToolViewModel
                {
                    Name = tool.Name!,
                    Description = tool.Description ?? string.Empty,
                    Schema = tool.Schema,
                    // A tool that was explicitly turned off stays off across a refresh; new tools default to on.
                    Enabled = previous.TryGetValue(tool.Name!, out var old) ? old.Enabled : true,
                });
            }
        }

        /// <summary>Splits the arguments box into the argv array, honoring double-quoted tokens.</summary>
        internal static string[] SplitArguments(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            foreach (var ch in text!)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (char.IsWhiteSpace(ch) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens.ToArray();
        }

        /// <summary>Re-renders an argv array for the arguments box, quoting tokens that contain spaces.</summary>
        internal static string JoinArguments(IEnumerable<string>? args)
            => string.Join(" ", (args ?? Array.Empty<string>()).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
