using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class TargetSettingsWindow : Window
{
    private readonly CustomTarget? _target;

    public TargetSettingsWindow(
        IReadOnlyList<ProjectOption> projects,
        CustomTarget? target = null,
        Guid? fixedProjectId = null,
        CustomTargetCadence? initialCadence = null)
    {
        _target = target;
        InitializeComponent();
        HeadingText.Text = target is null ? "Add target" : "Edit target";
        SaveButton.Content = target is null ? "Add target" : "Save target";
        var projectChoices = projects.Select(project => new TargetProjectChoice(
                project.ProjectId,
                $"{project.ProjectName} \u00B7 {project.ClientName}"))
            .ToArray();
        ProjectCombo.ItemsSource = fixedProjectId is { } scopedProjectId
            ? projectChoices.Where(choice => choice.ProjectId == scopedProjectId).ToArray()
            : new[] { new TargetProjectChoice(null, "All projects") }
                .Concat(projectChoices)
                .ToArray();
        if (ProjectCombo.Items.Count == 0)
        {
            throw new ArgumentException("The fixed project is not available.", nameof(fixedProjectId));
        }

        ProjectCombo.IsEnabled = fixedProjectId is null;
        if (fixedProjectId is not null)
        {
            ScopeHelpText.Text =
                "This target belongs to the project. Its type, name, and hours can be changed here.";
        }

        CadenceCombo.ItemsSource = new[]
        {
            new TargetCadenceChoice(CustomTargetCadence.Daily, "Daily"),
            new TargetCadenceChoice(CustomTargetCadence.Weekly, "Weekly"),
            new TargetCadenceChoice(CustomTargetCadence.Monthly, "Monthly"),
            new TargetCadenceChoice(CustomTargetCadence.OneTime, "One time"),
        };
        DurationMetricCombo.ItemsSource = new[]
        {
            new TargetDurationMetricChoice(TargetDurationMetric.ActiveTime, "Active time only"),
            new TargetDurationMetricChoice(TargetDurationMetric.IncludingShortIdle, "Time including short idle"),
        };

        ProjectCombo.SelectedItem = ((IEnumerable<TargetProjectChoice>)ProjectCombo.ItemsSource)
            .FirstOrDefault(choice => choice.ProjectId == (fixedProjectId ?? target?.ProjectId))
            ?? ProjectCombo.Items.Cast<TargetProjectChoice>().First();
        CadenceCombo.SelectedItem = ((IEnumerable<TargetCadenceChoice>)CadenceCombo.ItemsSource)
            .First(choice => choice.Cadence ==
                (target?.Cadence ?? initialCadence ?? CustomTargetCadence.Daily));
        DurationMetricCombo.SelectedItem = ((IEnumerable<TargetDurationMetricChoice>)DurationMetricCombo.ItemsSource)
            .First(choice => choice.Metric == (target?.DurationMetric ?? TargetDurationMetric.ActiveTime));
        NameText.Text = target?.Name ?? string.Empty;
        HoursText.Text = target?.TargetHours.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    public TargetSettingsResult? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectCombo.SelectedItem is not TargetProjectChoice project ||
            CadenceCombo.SelectedItem is not TargetCadenceChoice cadence ||
            DurationMetricCombo.SelectedItem is not TargetDurationMetricChoice durationMetric)
        {
            ValidationText.Text = "Choose a project scope and target type.";
            return;
        }

        if ((!double.TryParse(HoursText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours) &&
             !double.TryParse(HoursText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out hours)) ||
            !double.IsFinite(hours) || hours <= 0)
        {
            ValidationText.Text = "Enter hours greater than zero.";
            return;
        }

        var name = NameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var scope = project.ProjectId is null ? "All projects" : project.DisplayName.Split('·')[0].Trim();
            var cadenceText = cadence.Cadence == CustomTargetCadence.OneTime
                ? "one-time target"
                : $"{cadence.DisplayName.ToLowerInvariant()} target";
            name = $"{scope} {cadenceText}";
        }

        Result = new TargetSettingsResult(
            name,
            project.ProjectId,
            cadence.Cadence,
            hours,
            durationMetric.Metric);
        DialogResult = true;
    }

    private sealed record TargetProjectChoice(Guid? ProjectId, string DisplayName);
    private sealed record TargetCadenceChoice(CustomTargetCadence Cadence, string DisplayName);
    private sealed record TargetDurationMetricChoice(TargetDurationMetric Metric, string DisplayName);
}

public sealed record TargetSettingsResult(
    string Name,
    Guid? ProjectId,
    CustomTargetCadence Cadence,
    double TargetHours,
    TargetDurationMetric DurationMetric);
