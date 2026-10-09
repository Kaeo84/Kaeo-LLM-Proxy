using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A single tool advertised by an MCP server. <see cref="Enabled"/> decides whether the
    /// tool is offered to the model at all; the schema is carried along untouched so a settings
    /// save round-trip does not lose the definition pulled from the server.
    /// </summary>
    public sealed class McpToolViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _description = string.Empty;
        private bool _enabled = true;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        /// <summary>Whether the model may call this tool.</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        /// <summary>JSON schema for the tool's arguments, as reported by the server.</summary>
        public JsonNode? Schema { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
