using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class ProjectSettingsWindow : Window
{
    private readonly Project _project;
    private readonly IReadOnlyList<ProjectTargetDebtCancellation> _activeDebtCancellations;
    private readonly ObservableCollection<ProjectTargetDraftRow> _targets = [];
    private bool _restoreCanceledDebt;

    public ProjectSettingsWindow(
        Project project,
        string clientName,
        IReadOnlyList<Client> clients,
        IReadOnlyList<ProjectTargetDebtCancellation>? activeDebtCancellations = null,
        IReadOnlyList<CustomTarget>? targets = null)
    {
        _project = project;
        InitializeComponent();
        _activeDebtCancellations = activeDebtCancellations ?? [];
        HeadingText.Text = $"{clientName} / {project.Name}";
        ClientCombo.ItemsSource = clients;
        ClientCombo.SelectedValue = project.ClientId;
        CarryOverTargetDebtCheck.IsChecked = project.CarryOverTargetDebtEnabled;
        HourlyRateText.Text = project.HourlyRate?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        CurrencyCombo.ItemsSource = new[] { "PLN", "USD", "EUR" };
        CurrencyCombo.SelectedItem = project.Currency;

        var initialTargets = targets ?? CreateLegacyTargetDrafts(project);
        foreach (var target in initialTargets.Where(target => target.ProjectId == project.Id))
        {
            _targets.Add(ProjectTargetDraftRow.FromTarget(target));
        }

        ProjectTargetsGrid.ItemsSource = _targets;
        RefreshGhostTargets();
        UpdateCanceledDebtPanel();
    }

    public ProjectSettingsResult? Result { get; private set; }

    internal void SetClientForPreview(Guid clientId) => ClientCombo.SelectedValue = clientId;

    internal void SetTargetsForPreview(double daily, double weekly, double monthly)
    {
        for (var index = _targets.Count - 1; index >= 0; index--)
        {
            if (!_targets[index].IsGhost &&
                _targets[index].Cadence is not CustomTargetCadence.OneTime)
            {
                _targets.RemoveAt(index);
            }
        }

        _targets.Add(ProjectTargetDraftRow.New("Daily target", CustomTargetCadence.Daily, daily));
        _targets.Add(ProjectTargetDraftRow.New("Weekly target", CustomTargetCadence.Weekly, weekly));
        _targets.Add(ProjectTargetDraftRow.New("Monthly target", CustomTargetCadence.Monthly, monthly));
        RefreshGhostTargets();
    }

    internal void SetCarryOverTargetDebtForPreview(bool enabled) =>
        CarryOverTargetDebtCheck.IsChecked = enabled;

    internal void RestoreCanceledDebtForPreview() => MarkCanceledDebtForRestore();

    internal void SubmitForPreview() => Submit(closeDialog: false);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Submit(closeDialog: true);
    }

    private void RestoreCanceledDebt_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        MarkCanceledDebtForRestore();
    }

    private void AddProjectTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowTargetEditor(target: null, CustomTargetCadence.Daily);
    }

    private void EditProjectTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectTargetsGrid.SelectedItem is ProjectTargetDraftRow target)
        {
            ShowTargetEditor(target, target.Cadence);
        }
    }

    private void RemoveProjectTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectTargetsGrid.SelectedItem is not ProjectTargetDraftRow { IsGhost: false } target)
        {
            return;
        }

        _targets.Remove(target);
        RefreshGhostTargets();
    }

    private void ProjectTargetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (FindTargetRow(e.OriginalSource as DependencyObject) is not { } target)
        {
            return;
        }

        e.Handled = true;
        ProjectTargetsGrid.SelectedItem = target;
        ShowTargetEditor(target, target.Cadence);
    }

    private void ProjectTargetsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        ProjectTargetsGrid.SelectedItem = FindTargetRow(e.OriginalSource as DependencyObject);
    }

    private void ProjectTargetsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = sender;
        _ = e;
        var selected = ProjectTargetsGrid.SelectedItem as ProjectTargetDraftRow;
        EditProjectTargetMenuItem.Visibility = selected is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RemoveProjectTargetSeparator.Visibility = selected is { IsGhost: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveProjectTargetMenuItem.Visibility = selected is { IsGhost: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private ProjectTargetDraftRow? FindTargetRow(DependencyObject? source)
    {
        if (source is null)
        {
            return null;
        }

        return ItemsControl.ContainerFromElement(ProjectTargetsGrid, source) is DataGridRow row
            ? row.Item as ProjectTargetDraftRow
            : null;
    }

    private void ShowTargetEditor(
        ProjectTargetDraftRow? target,
        CustomTargetCadence initialCadence)
    {
        var selectedClientName = (ClientCombo.SelectedItem as Client)?.Name ?? "Client";
        var projectOption = new ProjectOption(
            _project.Id,
            ClientCombo.SelectedValue is Guid clientId ? clientId : _project.ClientId,
            selectedClientName,
            _project.Name,
            _project.Color);
        CustomTarget? editableTarget = null;
        if (target is { IsGhost: false, TargetHours: { } targetHours })
        {
            var timestamp = target.CreatedUtc ?? DateTimeOffset.UtcNow;
            editableTarget = new CustomTarget(
                target.Id ?? Guid.NewGuid(),
                target.Name,
                _project.Id,
                target.Cadence,
                targetHours,
                timestamp,
                timestamp,
                DurationMetric: target.DurationMetric);
        }

        var dialog = new TargetSettingsWindow(
            [projectOption],
            editableTarget,
            fixedProjectId: _project.Id,
            initialCadence)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        var updated = new ProjectTargetDraftRow(
            target is { IsGhost: false } ? target.Id : null,
            result.Name,
            result.Cadence,
            result.TargetHours,
            target is { IsGhost: false } ? target.CreatedUtc : null,
            result.DurationMetric,
            IsGhost: false);
        if (target is null)
        {
            _targets.Add(updated);
        }
        else
        {
            var index = _targets.IndexOf(target);
            if (index >= 0)
            {
                _targets[index] = updated;
            }
        }

        RefreshGhostTargets();
        ProjectTargetsGrid.SelectedItem = updated;
        ProjectTargetsGrid.ScrollIntoView(updated);
    }

    private void RefreshGhostTargets()
    {
        var realTargets = _targets.Where(target => !target.IsGhost).ToArray();
        var refreshed = new List<ProjectTargetDraftRow>(realTargets);
        foreach (var cadence in new[]
                 {
                     CustomTargetCadence.Daily,
                     CustomTargetCadence.Weekly,
                     CustomTargetCadence.Monthly,
                 })
        {
            if (realTargets.All(target => target.Cadence != cadence))
            {
                refreshed.Add(ProjectTargetDraftRow.Ghost(cadence));
            }
        }

        var ordered = refreshed
            .OrderBy(target => target.Cadence)
            .ThenBy(target => target.IsGhost)
            .ThenBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _targets.Clear();
        foreach (var target in ordered)
        {
            _targets.Add(target);
        }
    }

    private void MarkCanceledDebtForRestore()
    {
        if (_activeDebtCancellations.Count == 0)
        {
            return;
        }

        _restoreCanceledDebt = true;
        RestoreCanceledDebtButton.IsEnabled = false;
        RestoreCanceledDebtButton.Content = "Will be restored";
        CanceledDebtText.Text = $"{FormatCanceledDebt()} will be brought back when settings are saved.";
    }

    private void UpdateCanceledDebtPanel()
    {
        if (_activeDebtCancellations.Count == 0)
        {
            CanceledDebtPanel.Visibility = Visibility.Collapsed;
            return;
        }

        CanceledDebtPanel.Visibility = Visibility.Visible;
        CanceledDebtText.Text = $"{FormatCanceledDebt()}.";
    }

    private string FormatCanceledDebt()
    {
        var totalSeconds = _activeDebtCancellations.Sum(item => item.CanceledSeconds);
        var lastCanceled = _activeDebtCancellations.Max(item => item.CanceledUtc).ToLocalTime();
        var amount = TargetDebtText.Format(totalSeconds);
        return _activeDebtCancellations.Count == 1
            ? $"{amount} removed from debt on {AppTextCulture.FormatShortDate(lastCanceled)}"
            : $"{amount} across {_activeDebtCancellations.Count} debt adjustments; last changed on {AppTextCulture.FormatShortDate(lastCanceled)}";
    }

    private void Submit(bool closeDialog)
    {
        if (ClientCombo.SelectedValue is not Guid clientId)
        {
            ValidationText.Text = "Choose a client.";
            return;
        }

        if (!TryParseOptionalDecimal(HourlyRateText.Text, "hourly rate", out var rate))
        {
            return;
        }

        if (CurrencyCombo.SelectedItem is not string currency)
        {
            ValidationText.Text = "Choose PLN, USD, or EUR.";
            return;
        }

        var targetInputs = _targets
            .Where(target => !target.IsGhost && target.TargetHours is > 0)
            .Select(target => new ProjectTargetInput(
                target.Id,
                target.Name,
                target.Cadence,
                target.TargetHours!.Value,
                target.DurationMetric))
            .ToArray();
        var daily = SumTargets(targetInputs, CustomTargetCadence.Daily);
        var weekly = SumTargets(targetInputs, CustomTargetCadence.Weekly);
        var monthly = SumTargets(targetInputs, CustomTargetCadence.Monthly);
        var carryOverTargetDebt = CarryOverTargetDebtCheck.IsChecked == true;
        if (carryOverTargetDebt && monthly is null)
        {
            ValidationText.Text = "Add at least one monthly target before carrying target debt forward.";
            return;
        }

        ValidationText.Text = string.Empty;
        Result = new ProjectSettingsResult(
            clientId,
            daily,
            weekly,
            monthly,
            rate,
            currency,
            carryOverTargetDebt,
            _restoreCanceledDebt,
            targetInputs);
        if (closeDialog)
        {
            DialogResult = true;
        }
    }

    private bool TryParseOptionalDecimal(string text, string label, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign |
                                    NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if ((!decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out var parsed) &&
             !decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out parsed)) || parsed <= 0)
        {
            ValidationText.Text = $"Enter an {label} greater than zero, or leave it blank.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static double? SumTargets(
        IReadOnlyList<ProjectTargetInput> targets,
        CustomTargetCadence cadence)
    {
        var matching = targets.Where(target => target.Cadence == cadence).ToArray();
        return matching.Length == 0 ? null : matching.Sum(target => target.TargetHours);
    }

    private static IReadOnlyList<CustomTarget> CreateLegacyTargetDrafts(Project project)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var targets = new List<CustomTarget>();
        Add(project.DailyTargetHours, CustomTargetCadence.Daily);
        Add(project.WeeklyTargetHours, CustomTargetCadence.Weekly);
        Add(project.MonthlyTargetHours, CustomTargetCadence.Monthly);
        return targets;

        void Add(double? hours, CustomTargetCadence cadence)
        {
            if (hours is null)
            {
                return;
            }

            targets.Add(new CustomTarget(
                Guid.NewGuid(),
                $"{cadence} target",
                project.Id,
                cadence,
                hours.Value,
                nowUtc,
                nowUtc,
                DurationMetric: TargetDurationMetric.ActiveTime));
        }
    }
}

public sealed record ProjectSettingsResult(
    Guid ClientId,
    double? DailyTargetHours,
    double? WeeklyTargetHours,
    double? MonthlyTargetHours,
    decimal? HourlyRate,
    string Currency,
    bool CarryOverTargetDebtEnabled,
    bool RestoreCanceledDebt,
    IReadOnlyList<ProjectTargetInput> Targets);

public sealed record ProjectTargetDraftRow(
    Guid? Id,
    string Name,
    CustomTargetCadence Cadence,
    double? TargetHours,
    DateTimeOffset? CreatedUtc,
    TargetDurationMetric DurationMetric,
    bool IsGhost)
{
    public string Type => Cadence switch
    {
        CustomTargetCadence.Daily => "Daily",
        CustomTargetCadence.Weekly => "Weekly",
        CustomTargetCadence.Monthly => "Monthly",
        CustomTargetCadence.OneTime => "One time",
        _ => "Unknown",
    };

    public string TargetTime => TargetHours is { } hours
        ? $"{hours:0.##} h"
        : string.Empty;

    public string DurationMetricText => DurationMetric == TargetDurationMetric.IncludingShortIdle
        ? "Time + short idle"
        : "Active time";

    public static ProjectTargetDraftRow FromTarget(CustomTarget target) =>
        new(
            target.Id,
            target.Name,
            target.Cadence,
            target.TargetHours,
            target.CreatedUtc,
            target.DurationMetric,
            IsGhost: false);

    public static ProjectTargetDraftRow New(
        string name,
        CustomTargetCadence cadence,
        double hours,
        TargetDurationMetric durationMetric = TargetDurationMetric.ActiveTime) =>
        new(null, name, cadence, hours, null, durationMetric, IsGhost: false);

    public static ProjectTargetDraftRow Ghost(CustomTargetCadence cadence) =>
        new(
            null,
            $"{cadence} target",
            cadence,
            null,
            null,
            TargetDurationMetric.ActiveTime,
            IsGhost: true);
}
