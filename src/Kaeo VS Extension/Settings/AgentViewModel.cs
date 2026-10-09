using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A single agent entry in the settings window's Agents tab.
    /// <see cref="Tools"/> and <see cref="DefaultModel"/> are carried through untouched:
    /// the tab only edits name, description, and system prompt.
    /// </summary>
    public sealed class AgentViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string? _description;
        private string _systemPrompt = string.Empty;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string? Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        /// <summary>The agent's system prompt, edited in the large text area.</summary>
        public string SystemPrompt
        {
            get => _systemPrompt;
            set { _systemPrompt = value; OnPropertyChanged(); }
        }

        /// <summary>Tool names this agent may use; null = all tools. Not edited in this tab.</summary>
        public string[]? Tools { get; set; }

        /// <summary>Model label this agent prefers; not edited in this tab.</summary>
        public string? DefaultModel { get; set; }

        /// <summary>True for the built-in Agent/Ask/Plan entries, which can be reverted to defaults.</summary>
        public bool IsBuiltin { get; set; }

        /// <summary>Shipped description for built-ins; null for user agents.</summary>
        public string? DefaultDescription { get; set; }

        /// <summary>Shipped system prompt for built-ins; null for user agents.</summary>
        public string? DefaultSystemPrompt { get; set; }

        private bool _isDefault;

        /// <summary>True when this agent is the default the tool window starts on (mirrors the pinned model).</summary>
        public bool IsDefault
        {
            get => _isDefault;
            set { _isDefault = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
