using System.Windows;

namespace ProjectTimeTracker.Windows.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueText.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueText.Focus();
            ValueText.SelectAll();
        };
    }

    public string Value => ValueText.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (string.IsNullOrWhiteSpace(ValueText.Text))
        {
            ValidationText.Text = "A value is required.";
            return;
        }

        DialogResult = true;
    }
}
