using System.Windows;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class ProjectChooserWindow : Window
{
    public ProjectChooserWindow(IReadOnlyList<RecognitionCandidate> candidates)
    {
        InitializeComponent();
        Candidates = candidates
            .GroupBy(candidate => candidate.Project.Id)
            .Select(group => group.First())
            .Select(candidate => new Choice(candidate, $"{candidate.Client.Name} / {candidate.Project.Name}"))
            .ToArray();
        ProjectsList.ItemsSource = Candidates;
        ProjectsList.SelectedIndex = Candidates.Count > 0 ? 0 : -1;
    }

    private IReadOnlyList<Choice> Candidates { get; }
    public RecognitionCandidate? SelectedCandidate { get; private set; }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectsList.SelectedItem is Choice choice)
        {
            SelectedCandidate = choice.Candidate;
            DialogResult = true;
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
    }

    private sealed record Choice(RecognitionCandidate Candidate, string DisplayName);
}
