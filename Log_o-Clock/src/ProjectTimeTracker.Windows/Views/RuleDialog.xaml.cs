using System.Windows;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class RuleDialog : Window
{
    private readonly Func<WindowActivity?> _captureCurrentActivity;

    public RuleDialog(
        IReadOnlyList<ProjectOption> projects,
        Guid? selectedProjectId,
        string titlePattern,
        string? processName,
        Func<WindowActivity?> captureCurrentActivity,
        bool isEditing)
    {
        InitializeComponent();
        _captureCurrentActivity = captureCurrentActivity;
        ProjectCombo.ItemsSource = projects;
        ProjectCombo.SelectedValue = selectedProjectId ?? projects.FirstOrDefault()?.ProjectId;
        PatternText.Text = titlePattern;
        ProcessText.Text = processName ?? string.Empty;
        HeadingText.Text = isEditing ? "Edit window rule" : "Add window rule";
        Title = HeadingText.Text;
        Loaded += (_, _) =>
        {
            if (isEditing)
            {
                PatternText.Focus();
            }
            else
            {
                ProjectCombo.Focus();
            }
        };
    }

    public Guid? ProjectId => ProjectCombo.SelectedValue is Guid id ? id : null;
    public string TitlePattern => PatternText.Text.Trim();
    public string? ProcessName => string.IsNullOrWhiteSpace(ProcessText.Text) ? null : ProcessText.Text.Trim();

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "Switch to the target window now. Capturing in 3 seconds…";
        WindowState = WindowState.Minimized;

        await Task.Delay(TimeSpan.FromSeconds(3));
        var activity = _captureCurrentActivity();

        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        CaptureButton.IsEnabled = true;
        if (activity is null)
        {
            CaptureStatusText.Text = "No foreground window could be captured.";
            return;
        }

        PatternText.Text = activity.Title;
        ProcessText.Text = activity.ProcessName;
        CaptureStatusText.Text = $"Captured {activity.ProcessName}. Edit the title phrase if only part of it should match.";
        PatternText.Focus();
        PatternText.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectId is null)
        {
            ValidationText.Text = "Select a project.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PatternText.Text))
        {
            ValidationText.Text = "A window-title phrase is required.";
            return;
        }

        DialogResult = true;
    }
}
