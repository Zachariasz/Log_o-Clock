using System.Windows;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class NewProjectWindow : Window
{
    private string _selectedColorHex = "#FF7356";

    public NewProjectWindow(IReadOnlyList<Client> clients, Guid? preferredClientId = null)
    {
        InitializeComponent();
        ClientCombo.ItemsSource = clients;
        ClientCombo.SelectedValue = preferredClientId ?? clients.FirstOrDefault()?.Id;
        Loaded += (_, _) => ProjectNameText.Focus();
    }

    public NewProjectResult? Result { get; private set; }

    internal void SetProjectForPreview(Guid clientId, string projectName, string color)
    {
        ClientCombo.SelectedValue = clientId;
        ProjectNameText.Text = projectName;
        SetSelectedColor(color);
    }

    internal void SubmitForPreview() => Submit(closeDialog: false);

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var projectName = ProjectNameText.Text.Trim();
        var dialog = new ProjectColorWindow(
            "Project color",
            projectName.Length == 0 ? "New project" : projectName,
            _selectedColorHex)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            SetSelectedColor(dialog.SelectedColorHex);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Submit(closeDialog: true);
    }

    private void Submit(bool closeDialog)
    {
        if (ClientCombo.SelectedValue is not Guid clientId)
        {
            ValidationText.Text = "Choose a client.";
            return;
        }

        var name = ProjectNameText.Text.Trim();
        if (name.Length == 0)
        {
            ValidationText.Text = "Enter a project name.";
            ProjectNameText.Focus();
            return;
        }

        Result = new NewProjectResult(clientId, name, _selectedColorHex);
        if (closeDialog)
        {
            DialogResult = true;
        }
    }

    private void SetSelectedColor(string colorText)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorText);
        _selectedColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorSwatch.Background = new SolidColorBrush(color);
        ColorHexText.Text = _selectedColorHex;
    }
}

public sealed record NewProjectResult(Guid ClientId, string ProjectName, string Color);
