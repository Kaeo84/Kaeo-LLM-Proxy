using System.Collections.Generic;
using System.Windows;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    public partial class InquiryDialog : Window
    {
        public string Response { get; private set; }
        public int? SelectedSuggestionIndex { get; private set; }

        public InquiryDialog()
        {
            InitializeComponent();
        }

        public InquiryDialog(string question, List<string> suggestions = null)
            : this()
        {
            QuestionText.Text = question;

            if (suggestions != null && suggestions.Count > 0)
            {
                SuggestionsList.ItemsSource = suggestions;
                SuggestionsList.Visibility = Visibility.Visible;
                SuggestionsList.SelectionChanged += (s, e) =>
                {
                    if (SuggestionsList.SelectedIndex >= 0)
                    {
                        SelectedSuggestionIndex = SuggestionsList.SelectedIndex;
                        ResponseText.Text = suggestions[SuggestionsList.SelectedIndex];
                    }
                };
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Response = ResponseText.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Response = null;
            DialogResult = false;
            Close();
        }
    }
}
