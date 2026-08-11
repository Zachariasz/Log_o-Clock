using System.Windows;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class NewTaskWindow : Window
{
    public NewTaskWindow(IReadOnlyList<ProjectOption> projects, Guid? preferredProjectId = null)
    {
        InitializeComponent();
        ProjectCombo.ItemsSource = projects;
        ProjectCombo.SelectedValue = preferredProjectId ?? projects.FirstOrDefault()?.ProjectId;
        Loaded += (_, _) => TaskNameText.Focus();
    }

    public NewTaskResult? Result { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectCombo.SelectedValue is not Guid projectId)
        {
            ValidationText.Text = "Choose a project.";
            return;
        }

        var name = TaskNameText.Text.Trim();
        if (name.Length == 0)
        {
            ValidationText.Text = "Enter a task name.";
            TaskNameText.Focus();
            return;
        }

        Result = new NewTaskResult(projectId, name);
        DialogResult = true;
    }
}

public sealed record NewTaskResult(Guid ProjectId, string TaskName);
