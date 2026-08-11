using System.Windows;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class TagSettingsWindow : Window
{
    private static readonly ProjectOption GlobalScopeOption = new(
        SystemEntityIds.GlobalTagScopeId,
        Guid.Empty,
        "Global · every project",
        "All projects",
        "#766F80");
    private static readonly ProjectOption MultipleProjectsScopeOption = new(
        Guid.Empty,
        Guid.Empty,
        "Current scope",
        "Multiple projects",
        "#766F80");

    private string _selectedColorHex;

    public TagSettingsWindow(
        IReadOnlyList<ProjectOption> projects,
        TagDefinition? tag = null,
        string suggestedColor = "#339CFF")
    {
        InitializeComponent();
        HeadingText.Text = tag is null ? "Add tag" : "Edit tag";
        Title = HeadingText.Text;
        SaveButton.Content = tag is null ? "Add tag" : "Save changes";
        TagNameText.Text = tag?.Name ?? string.Empty;
        var hasMultipleProjects = tag?.IsGlobal == false && tag.AssignedProjectIds.Count > 1;
        ScopeCombo.ItemsSource = new[] { GlobalScopeOption }
            .Concat(hasMultipleProjects ? [MultipleProjectsScopeOption] : [])
            .Concat(projects)
            .ToArray();
        ScopeCombo.SelectedValue = tag?.IsGlobal == true
            ? SystemEntityIds.GlobalTagScopeId
            : hasMultipleProjects
                ? Guid.Empty
                : tag?.AssignedProjectIds.FirstOrDefault()
              ?? projects.FirstOrDefault()?.ProjectId
              ?? SystemEntityIds.GlobalTagScopeId;
        _selectedColorHex = tag?.Color ?? suggestedColor;
        SetSelectedColor(_selectedColorHex);
        Loaded += (_, _) => TagNameText.Focus();
    }

    public TagSettingsResult? Result { get; private set; }

    internal void SetValuesForPreview(string name, Guid scopeId, string color)
    {
        TagNameText.Text = name;
        ScopeCombo.SelectedValue = scopeId;
        SetSelectedColor(color);
    }

    internal bool SubmitForPreview() => Submit(closeDialog: false);

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new ProjectColorWindow(
            "Tag color",
            string.IsNullOrWhiteSpace(TagNameText.Text) ? "New tag" : TagNameText.Text.Trim(),
            _selectedColorHex)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            SetSelectedColor(dialog.SelectedColorHex);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = Submit(closeDialog: true);
    }

    private bool Submit(bool closeDialog)
    {
        var name = TagParser.Normalize(TagNameText.Text);
        if (name is null)
        {
            ValidationText.Text = "Use letters, numbers, underscores, or hyphens in the tag name.";
            TagNameText.Focus();
            return false;
        }

        if (ScopeCombo.SelectedValue is not Guid scopeId)
        {
            ValidationText.Text = "Choose Global (all projects) or a project.";
            return false;
        }

        Result = new TagSettingsResult(
            name,
            _selectedColorHex,
            scopeId == SystemEntityIds.GlobalTagScopeId ? null : scopeId,
            PreserveExistingScope: scopeId == Guid.Empty);
        if (closeDialog)
        {
            DialogResult = true;
        }

        return true;
    }

    private void SetSelectedColor(string colorText)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorText);
        _selectedColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorSwatch.Background = new SolidColorBrush(color);
        ColorHexText.Text = _selectedColorHex;
    }
}

public sealed record TagSettingsResult(
    string Name,
    string Color,
    Guid? ProjectId,
    bool PreserveExistingScope = false);
