using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Kaeo.LlmProxy.VSExtension.Core;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A foldable connection section in the settings window. Holds the connection's
    /// editable fields and its live model list. <see cref="RefreshAsync"/> pulls the
    /// model list from the configured upstream (Ollama today; OpenAI/Anthropic stubbed).
    /// </summary>
    public sealed class ConnectionViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _url = string.Empty;
        private string _apiKey = string.Empty;
        private string _upstream = "Ollama";
        private bool _enabled = true;
        private bool _isExpanded;
        private bool _isRefreshing;
        private string? _refreshError;

        public ConnectionViewModel()
        {
            Models = new ObservableCollection<ModelViewModel>();
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        /// <summary>Base URL of the Ollama server. Accepts http:// and https://.</summary>
        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value; OnPropertyChanged(); }
        }

        /// <summary>Upstream API flavor: Ollama today, OpenAI/Anthropic stubbed for later.</summary>
        public string Upstream
        {
            get => _upstream;
            set { _upstream = value; OnPropertyChanged(); }
        }

        /// <summary>Choices for the upstream dropdown.</summary>
        public IReadOnlyList<string> UpstreamOptions => UpstreamKinds.DisplayNames;

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        /// <summary>Controls the Expander open/closed state.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
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

        /// <summary>Last refresh error message, or null when the last refresh succeeded.</summary>
        public string? RefreshError
        {
            get => _refreshError;
            set
            {
                _refreshError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        /// <summary>True when <see cref="RefreshError"/> holds a non-empty message.</summary>
        public bool HasError => !string.IsNullOrEmpty(_refreshError);

        /// <summary>True when a refresh is not in flight (drives the Refresh button's IsEnabled).</summary>
        public bool CanRefresh => !IsRefreshing;

        /// <summary>Live model list shown in the table below the connection header.</summary>
        public ObservableCollection<ModelViewModel> Models { get; }

        /// <summary>
        /// Pulls the model list from the configured upstream and repopulates
        /// <see cref="Models"/>, restoring each model's enabled/pinned state from the
        /// previous list so a refresh does not wipe user selections.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            RefreshError = null;
            try
            {
                if (string.IsNullOrWhiteSpace(Url))
                {
                    RefreshError = "No URL set.";
                    return;
                }
                if (!Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshError = "URL must start with http:// or https://.";
                    return;
                }

                var client = UpstreamClientFactory.Create(UpstreamKinds.Parse(Upstream), Url, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey);
                var fetched = await client.GetModelsAsync();

                var previous = Models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                Models.Clear();
                foreach (var m in fetched)
                {
                    var vm = new ModelViewModel
                    {
                        Name = m.Name,
                        ModelId = m.Name,
                        Capabilities = string.Join(", ", m.Capabilities),
                    };
                    if (previous.TryGetValue(m.Name, out var old))
                    {
                        vm.Enabled = old.Enabled;
                        vm.IsPinned = old.IsPinned;
                    }
                    Models.Add(vm);
                }
            }
            catch (Exception ex)
            {
                RefreshError = ex.Message;
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
