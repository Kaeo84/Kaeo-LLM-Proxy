using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A single model row in the settings window's model table.
    /// <see cref="IsPinned"/> marks this model as the connection's default.
    /// </summary>
    public sealed class ModelViewModel : INotifyPropertyChanged
    {
        /// <summary>Values offered by the grid's Reasoning source column (ModelEntry.ReasoningSource).</summary>
        public static IReadOnlyList<string> ReasoningSourceOptions { get; } = new[] { "Auto", "ThinkingField", "InlineTags", "Disabled" };

        private string _name = string.Empty;
        private string _modelId = string.Empty;
        private string _capabilities = string.Empty;
        private bool _enabled;
        private bool _isPinned;
        private string _reasoningSource = "Auto";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        /// <summary>Model identifier as reported by the Ollama API (may equal <see cref="Name"/>).</summary>
        public string ModelId
        {
            get => _modelId;
            set { _modelId = value; OnPropertyChanged(); }
        }

        /// <summary>Comma-separated capability tokens (e.g. "tools, vision, completion").</summary>
        public string Capabilities
        {
            get => _capabilities;
            set { _capabilities = value; OnPropertyChanged(); }
        }

        /// <summary>Whether this model is available in the tool window dropdown.</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        /// <summary>Whether this model is the default for its connection.</summary>
        public bool IsPinned
        {
            get => _isPinned;
            set { _isPinned = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Where this model's reasoning (thinking) content arrives on the wire:
        /// "Auto", "ThinkingField", "InlineTags" or "Disabled". Conflicts between the
        /// structured thinking field and inline tags are resolved per model, not per connection.
        /// </summary>
        public string ReasoningSource
        {
            get => _reasoningSource;
            set { _reasoningSource = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
