using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class BulkEditWindow : Window
{
    private enum EditKind
    {
        Projects,
        Tasks,
        Tags,
        Rules,
    }

    private readonly EditKind _kind;
    private bool _initializing = true;
    private string _selectedColor = "#339CFF";

    private BulkEditWindow(EditKind kind, int count)
    {
        InitializeComponent();
        _kind = kind;
        HeadingText.Text = $"Edit {count} selected {GetPlural(kind)}";
        Title = HeadingText.Text;
        DescriptionText.Text =
            "Shared values are filled in. Fields marked “Multiple values” differ across the selection. " +
            "Only checked fields will be changed; names remain individual.";
        CurrencyCombo.ItemsSource = new[] { "PLN", "USD", "EUR" };
        HideAllRows();
    }

    public ProjectBulkEdit? ProjectEdit { get; private set; }
    public TaskBulkEdit? TaskEdit { get; private set; }
    public TagBulkEdit? TagEdit { get; private set; }
    public RecognitionRuleBulkEdit? RuleEdit { get; private set; }

    internal void VerifyProjectMixedValuesForPreview()
    {
        if (_kind != EditKind.Projects ||
            ClientMixedText.Visibility != Visibility.Visible ||
            ColorMixedText.Visibility != Visibility.Visible ||
            DailyMixedText.Visibility != Visibility.Visible ||
            MonthlyMixedText.Visibility != Visibility.Visible ||
            CarryDebtMixedText.Visibility != Visibility.Visible ||
            RateMixedText.Visibility != Visibility.Visible ||
            CurrencyMixedText.Visibility != Visibility.Visible ||
            WeeklyMixedText.Visibility != Visibility.Collapsed ||
            string.IsNullOrWhiteSpace(WeeklyText.Text) ||
            ApplyClientCheck.IsChecked == true ||
            ApplyDailyCheck.IsChecked == true ||
            ApplyWeeklyCheck.IsChecked == true)
        {
            throw new InvalidOperationException("The bulk editor did not distinguish shared values from multiple values.");
        }
    }

    public static BulkEditWindow ForProjects(
        IReadOnlyList<Project> projects,
        IReadOnlyList<Client> clients)
    {
        var dialog = new BulkEditWindow(EditKind.Projects, projects.Count);
        dialog.ClientRow.Visibility = Visibility.Visible;
        dialog.ColorRow.Visibility = Visibility.Visible;
        dialog.DailyRow.Visibility = Visibility.Visible;
        dialog.WeeklyRow.Visibility = Visibility.Visible;
        dialog.MonthlyRow.Visibility = Visibility.Visible;
        dialog.CarryDebtRow.Visibility = Visibility.Visible;
        dialog.RateRow.Visibility = Visibility.Visible;
        dialog.CurrencyRow.Visibility = Visibility.Visible;
        dialog.ClientCombo.ItemsSource = clients;

        dialog.SetCommonCombo(
            projects.Select(project => project.ClientId),
            dialog.ClientCombo,
            dialog.ClientMixedText);
        dialog.SetCommonColor(projects.Select(project => project.Color), dialog.ColorMixedText);
        dialog.SetCommonText(
            projects.Select(project => project.DailyTargetHours),
            value => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty,
            dialog.DailyText,
            dialog.DailyMixedText);
        dialog.SetCommonText(
            projects.Select(project => project.WeeklyTargetHours),
            value => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty,
            dialog.WeeklyText,
            dialog.WeeklyMixedText);
        dialog.SetCommonText(
            projects.Select(project => project.MonthlyTargetHours),
            value => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty,
            dialog.MonthlyText,
            dialog.MonthlyMixedText);
        dialog.SetCommonCheck(
            projects.Select(project => project.CarryOverTargetDebtEnabled),
            dialog.CarryDebtCheck,
            dialog.CarryDebtMixedText);
        dialog.SetCommonText(
            projects.Select(project => project.HourlyRate),
            value => value?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty,
            dialog.RateText,
            dialog.RateMixedText);
        dialog.SetCommonCombo(
            projects.Select(project => project.Currency),
            dialog.CurrencyCombo,
            dialog.CurrencyMixedText,
            StringComparer.OrdinalIgnoreCase);
        dialog.FinishInitialization();
        return dialog;
    }

    public static BulkEditWindow ForTasks(
        IReadOnlyList<SavedTask> tasks,
        IReadOnlyList<ProjectOption> projects)
    {
        var dialog = new BulkEditWindow(EditKind.Tasks, tasks.Count);
        dialog.ProjectRow.Visibility = Visibility.Visible;
        dialog.ProjectCombo.ItemsSource = projects;
        dialog.SetCommonCombo(
            tasks.Select(task => task.ProjectId),
            dialog.ProjectCombo,
            dialog.ProjectMixedText);
        dialog.FinishInitialization();
        return dialog;
    }

    public static BulkEditWindow ForTags(IReadOnlyList<TagDefinition> tags)
    {
        var dialog = new BulkEditWindow(EditKind.Tags, tags.Count);
        dialog.ColorRow.Visibility = Visibility.Visible;
        dialog.SetCommonColor(tags.Select(tag => tag.Color), dialog.ColorMixedText);
        dialog.FinishInitialization();
        return dialog;
    }

    public static BulkEditWindow ForRules(
        IReadOnlyList<RecognitionRule> rules,
        IReadOnlyList<ProjectOption> projects)
    {
        var dialog = new BulkEditWindow(EditKind.Rules, rules.Count);
        dialog.ProjectRow.Visibility = Visibility.Visible;
        dialog.PatternRow.Visibility = Visibility.Visible;
        dialog.ProcessRow.Visibility = Visibility.Visible;
        dialog.ProjectCombo.ItemsSource = projects;
        dialog.SetCommonCombo(
            rules.Select(rule => rule.ProjectId),
            dialog.ProjectCombo,
            dialog.ProjectMixedText);
        dialog.SetCommonText(
            rules.Select(rule => rule.TitlePattern),
            value => value,
            dialog.PatternText,
            dialog.PatternMixedText,
            StringComparer.OrdinalIgnoreCase);
        dialog.SetCommonText(
            rules.Select(rule => rule.ProcessName),
            value => value ?? string.Empty,
            dialog.ProcessText,
            dialog.ProcessMixedText,
            NullableOrdinalIgnoreCaseComparer.Instance);
        dialog.FinishInitialization();
        return dialog;
    }

    private static string GetPlural(EditKind kind) => kind switch
    {
        EditKind.Projects => "projects",
        EditKind.Tasks => "tasks",
        EditKind.Tags => "tags",
        EditKind.Rules => "window rules",
        _ => "objects",
    };

    private void HideAllRows()
    {
        foreach (var row in new[]
                 {
                     ProjectRow, ClientRow, ColorRow, DailyRow, WeeklyRow, MonthlyRow,
                     CarryDebtRow, RateRow, CurrencyRow, PatternRow, ProcessRow,
                 })
        {
            row.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishInitialization()
    {
        _initializing = false;
        Loaded += (_, _) =>
        {
            var firstVisibleControl = new Control[]
            {
                ProjectCombo, ClientCombo, ColorButton, DailyText, WeeklyText, MonthlyText,
                CarryDebtCheck, RateText, CurrencyCombo, PatternText, ProcessText,
            }.FirstOrDefault(control => control.IsVisible);
            firstVisibleControl?.Focus();
        };
    }

    private void SetCommonColor(IEnumerable<string> values, TextBlock mixedText)
    {
        var items = values.ToArray();
        var mixed = items.Skip(1).Any(value =>
            !string.Equals(value, items[0], StringComparison.OrdinalIgnoreCase));
        _selectedColor = mixed ? "#339CFF" : items[0];
        mixedText.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        UpdateColorPreview();
    }

    private void SetCommonText<T>(
        IEnumerable<T> values,
        Func<T, string> formatter,
        TextBox textBox,
        TextBlock mixedText,
        IEqualityComparer<T>? comparer = null)
    {
        var items = values.ToArray();
        comparer ??= EqualityComparer<T>.Default;
        var mixed = items.Skip(1).Any(value => !comparer.Equals(value, items[0]));
        textBox.Text = mixed ? string.Empty : formatter(items[0]);
        mixedText.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCommonCombo<T>(
        IEnumerable<T> values,
        ComboBox combo,
        TextBlock mixedText,
        IEqualityComparer<T>? comparer = null)
    {
        var items = values.ToArray();
        comparer ??= EqualityComparer<T>.Default;
        var mixed = items.Skip(1).Any(value => !comparer.Equals(value, items[0]));
        if (!mixed)
        {
            combo.SelectedValue = items[0];
            if (combo.SelectedValue is null)
            {
                combo.SelectedItem = items[0];
            }
        }

        mixedText.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCommonCheck(
        IEnumerable<bool> values,
        CheckBox checkBox,
        TextBlock mixedText)
    {
        var items = values.ToArray();
        var mixed = items.Skip(1).Any(value => value != items[0]);
        checkBox.IsChecked = mixed ? null : items[0];
        mixedText.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ValidationText.Text = string.Empty;

        switch (_kind)
        {
            case EditKind.Projects:
                if (!TryCreateProjectEdit(out var projectEdit))
                {
                    return;
                }

                ProjectEdit = projectEdit;
                break;
            case EditKind.Tasks:
                if (ApplyProjectCheck.IsChecked == true &&
                    ProjectCombo.SelectedValue is not Guid)
                {
                    ValidationText.Text = "Choose a project or leave Project unchecked.";
                    return;
                }

                TaskEdit = new TaskBulkEdit(
                    ApplyProjectCheck.IsChecked == true,
                    ProjectCombo.SelectedValue is Guid taskProjectId ? taskProjectId : null);
                break;
            case EditKind.Tags:
                TagEdit = new TagBulkEdit(
                    ApplyColorCheck.IsChecked == true,
                    _selectedColor);
                break;
            case EditKind.Rules:
                if (ApplyProjectCheck.IsChecked == true &&
                    ProjectCombo.SelectedValue is not Guid)
                {
                    ValidationText.Text = "Choose a project or leave Project unchecked.";
                    return;
                }

                if (ApplyPatternCheck.IsChecked == true &&
                    string.IsNullOrWhiteSpace(PatternText.Text))
                {
                    ValidationText.Text = "A window-title phrase cannot be empty.";
                    return;
                }

                RuleEdit = new RecognitionRuleBulkEdit(
                    ApplyProjectCheck.IsChecked == true,
                    ProjectCombo.SelectedValue is Guid ruleProjectId ? ruleProjectId : null,
                    ApplyPatternCheck.IsChecked == true,
                    PatternText.Text.Trim(),
                    ApplyProcessCheck.IsChecked == true,
                    string.IsNullOrWhiteSpace(ProcessText.Text) ? null : ProcessText.Text.Trim());
                break;
        }

        DialogResult = true;
    }

    private bool TryCreateProjectEdit(out ProjectBulkEdit edit)
    {
        edit = new ProjectBulkEdit();
        if (ApplyClientCheck.IsChecked == true && ClientCombo.SelectedValue is not Guid)
        {
            ValidationText.Text = "Choose a client or leave Client unchecked.";
            return false;
        }

        if (!TryParseOptionalDouble(ApplyDailyCheck, DailyText, "daily target", out var daily) ||
            !TryParseOptionalDouble(ApplyWeeklyCheck, WeeklyText, "weekly target", out var weekly) ||
            !TryParseOptionalDouble(ApplyMonthlyCheck, MonthlyText, "monthly target", out var monthly) ||
            !TryParseOptionalDecimal(ApplyRateCheck, RateText, "hourly rate", out var rate))
        {
            return false;
        }

        if (ApplyCurrencyCheck.IsChecked == true && CurrencyCombo.SelectedItem is not string)
        {
            ValidationText.Text = "Choose a currency or leave Currency unchecked.";
            return false;
        }

        edit = new ProjectBulkEdit(
            ApplyClientCheck.IsChecked == true,
            ClientCombo.SelectedValue is Guid projectClientId ? projectClientId : null,
            ApplyColorCheck.IsChecked == true,
            _selectedColor,
            ApplyDailyCheck.IsChecked == true,
            daily,
            ApplyWeeklyCheck.IsChecked == true,
            weekly,
            ApplyMonthlyCheck.IsChecked == true,
            monthly,
            ApplyRateCheck.IsChecked == true,
            rate,
            ApplyCurrencyCheck.IsChecked == true,
            CurrencyCombo.SelectedItem as string,
            ApplyCarryDebtCheck.IsChecked == true,
            CarryDebtCheck.IsChecked == true);
        return true;
    }

    private bool TryParseOptionalDouble(
        CheckBox applyCheck,
        TextBox textBox,
        string label,
        out double? value)
    {
        value = null;
        if (applyCheck.IsChecked != true || string.IsNullOrWhiteSpace(textBox.Text))
        {
            return true;
        }

        if ((!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
             !double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) ||
            !double.IsFinite(parsed) ||
            parsed <= 0)
        {
            ValidationText.Text = $"Enter a {label} greater than zero, clear it, or leave the field unchecked.";
            return false;
        }

        value = parsed;
        return true;
    }

    private bool TryParseOptionalDecimal(
        CheckBox applyCheck,
        TextBox textBox,
        string label,
        out decimal? value)
    {
        value = null;
        if (applyCheck.IsChecked != true || string.IsNullOrWhiteSpace(textBox.Text))
        {
            return true;
        }

        const NumberStyles styles = NumberStyles.AllowDecimalPoint |
                                    NumberStyles.AllowLeadingSign |
                                    NumberStyles.AllowLeadingWhite |
                                    NumberStyles.AllowTrailingWhite;
        if ((!decimal.TryParse(textBox.Text, styles, CultureInfo.CurrentCulture, out var parsed) &&
             !decimal.TryParse(textBox.Text, styles, CultureInfo.InvariantCulture, out parsed)) ||
            parsed <= 0)
        {
            ValidationText.Text = $"Enter an {label} greater than zero, clear it, or leave the field unchecked.";
            return false;
        }

        value = parsed;
        return true;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new ProjectColorWindow("Bulk color", HeadingText.Text, _selectedColor)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            _selectedColor = dialog.SelectedColorHex;
            ApplyColorCheck.IsChecked = true;
            ColorMixedText.Visibility = Visibility.Collapsed;
            UpdateColorPreview();
        }
    }

    private void UpdateColorPreview()
    {
        ColorValueText.Text = _selectedColor;
        ColorSwatch.Background = (Brush)new BrushConverter().ConvertFromString(_selectedColor)!;
    }

    private void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MarkChanged(ApplyProjectCheck, ProjectMixedText);

    private void ClientCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MarkChanged(ApplyClientCheck, ClientMixedText);

    private void CurrencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MarkChanged(ApplyCurrencyCheck, CurrencyMixedText);

    private void WeeklyText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyWeeklyCheck, WeeklyMixedText);

    private void DailyText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyDailyCheck, DailyMixedText);

    private void MonthlyText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyMonthlyCheck, MonthlyMixedText);

    private void RateText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyRateCheck, RateMixedText);

    private void CarryDebtCheck_Changed(object sender, RoutedEventArgs e) =>
        MarkChanged(ApplyCarryDebtCheck, CarryDebtMixedText);

    private void PatternText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyPatternCheck, PatternMixedText);

    private void ProcessText_TextChanged(object sender, TextChangedEventArgs e) =>
        MarkChanged(ApplyProcessCheck, ProcessMixedText);

    private void MarkChanged(CheckBox applyCheck, TextBlock mixedText)
    {
        if (_initializing)
        {
            return;
        }

        applyCheck.IsChecked = true;
        mixedText.Visibility = Visibility.Collapsed;
    }

    private sealed class NullableOrdinalIgnoreCaseComparer : IEqualityComparer<string?>
    {
        public static NullableOrdinalIgnoreCaseComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) =>
            string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string? obj) =>
            obj is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }
}
