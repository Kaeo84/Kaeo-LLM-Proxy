using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A single model row in the settings window's model table.
    /// <see cref="IsPinned"/> marks this model as the connection's default.
    /// </summary>
    public sealed class ModelViewModel : INotifyPropertyChanged
    {
        /// <summary>Values offered by the grid's Reasoning source column (ModelEntry.ReasoningSource).</summary>
        /// <remarks>
        /// These are the persisted keys, not display labels: they are written to the settings file and
        /// parsed by <c>ReasoningSources.Parse</c>, so renaming one would silently reset every saved
        /// model. The shared <see cref="ReasoningVocabulary"/> owns the human wording for the same
        /// axis and is the list this one is filtered against, so the proxy's Thinking Handling list
        /// and this column cannot drift into offering different concepts.
        /// </remarks>
        public static IReadOnlyList<string> ReasoningSourceOptions { get; } =
            new[] { "Auto", "ThinkingField", "InlineTags", "Disabled" }
                .Where(name => ReasoningVocabulary.ForReasoningSource(name) is not null)
                .ToArray();

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

        private long _contextSize;

        /// <summary>Model context window size in tokens (0 = unknown). Populated from
        /// the proxy's /api/tags details.context_length and editable in the grid.</summary>
        public long ContextSize
        {
            get => _contextSize;
            set { _contextSize = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
