using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class SoftwareSettingsWindow : Window
{
    private static readonly ProjectOption GlobalScopeOption = new(
        SystemEntityIds.GlobalSoftwareScopeId,
        Guid.Empty,
        "Global · every project",
        "No project",
        "#766F80");

    private readonly Func<WindowActivity?>? _captureCurrentActivity;
    private readonly ObservableCollection<TagDefinition> _allTags;
    private readonly ObservableCollection<TagDefinition> _tags;
    private bool _updatingNewTagText;

    public SoftwareSettingsWindow(
        ProjectSoftwareDefinition? setting,
        IReadOnlyList<TagDefinition> availableTags,
        IReadOnlyList<ProjectOption> projects,
        Guid? selectedProjectId = null,
        Func<WindowActivity?>? captureCurrentActivity = null)
    {
        InitializeComponent();
        _captureCurrentActivity = captureCurrentActivity;
        _allTags = new ObservableCollection<TagDefinition>(availableTags);
        _tags = [];
        var software = setting?.Software;
        Heading = software is null ? "Add software" : "Software settings";
        Title = Heading;
        HeadingText.Text = Heading;
        LabelText.Text = software?.Label ?? string.Empty;
        ProcessText.Text = software?.ProcessName ?? string.Empty;
        ProcessText.IsReadOnly = software is not null;
        ProcessText.Foreground = software is null
            ? (System.Windows.Media.Brush)FindResource("ContentPrimaryBrush")
            : (System.Windows.Media.Brush)FindResource("ContentSecondaryBrush");
        CaptureProcessButton.Visibility = software is null && captureCurrentActivity is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProjectCombo.ItemsSource = new[] { GlobalScopeOption }.Concat(projects).ToArray();
        ProjectCombo.SelectedValue = setting?.ProjectId
            ?? selectedProjectId
            ?? SystemEntityIds.GlobalSoftwareScopeId;
        ProjectCombo.IsEnabled = setting is null;
        UpdateScopeHelpText();
        ExcludedCheck.IsChecked = setting?.IsExcluded == true;
        UpdateCorrelatedTagsAvailability();
        TagsList.ItemsSource = _tags;
        var selectedIds = (setting?.Tags ?? []).Select(tag => tag.Id).ToHashSet();
        RefreshAvailableTags(selectedIds);
        Loaded += (_, _) => (software is null ? ProcessText : LabelText).Focus();
    }

    public string Heading { get; }
    public string ProcessName { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public bool IsExcluded { get; private set; }
    public IReadOnlyList<Guid> SelectedTagIds { get; private set; } = [];
    public IReadOnlyList<string> SelectedTagNames { get; private set; } = [];

    internal bool IsCaptureProcessAvailableForPreview =>
        CaptureProcessButton.Visibility == Visibility.Visible;

    internal bool HasGlobalScopeOptionForPreview =>
        ProjectCombo.Items.OfType<ProjectOption>()
            .Any(option => option.ProjectId == SystemEntityIds.GlobalSoftwareScopeId);

    internal string CapturedProcessForPreview => ProcessText.Text;

    internal string CapturedLabelForPreview => LabelText.Text;

    internal Task CaptureActiveProcessForPreviewAsync() =>
        CaptureActiveProcessAsync(TimeSpan.Zero, minimizeWindow: false);

    internal void TypeNewTagsForPreview(string text) => NewTagText.Text = text;

    internal IReadOnlyList<string> SelectedTagNamesForPreview =>
        TagsList.SelectedItems
            .OfType<TagDefinition>()
            .Select(tag => tag.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal Guid GetTagIdForPreview(string name) => _tags
        .Single(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
        .Id;

    internal bool PrepareSaveForPreview() => TryPrepareResult();

    internal bool IsCorrelatedTagsEditorEnabledForPreview =>
        CorrelatedTagsEditorPanel.IsEnabled && CorrelatedTagsListPanel.IsEnabled;

    internal bool AreCorrelatedTagsPanelsHiddenForPreview =>
        CorrelatedTagsEditorPanel.Visibility == Visibility.Collapsed &&
        CorrelatedTagsListPanel.Visibility == Visibility.Collapsed;

    internal bool HasThreeRowTagViewportForPreview =>
        CorrelatedTagsListPanel.ActualHeight >= 132d &&
        ScrollViewer.GetHorizontalScrollBarVisibility(TagsList) == ScrollBarVisibility.Disabled &&
        ScrollViewer.GetVerticalScrollBarVisibility(TagsList) == ScrollBarVisibility.Auto;

    internal void SetExcludedForPreview(bool isExcluded) =>
        ExcludedCheck.IsChecked = isExcluded;

    internal void VerifyExcludedToggleVisualStateForPreview()
    {
        ExcludedCheck.ApplyTemplate();
        var track = ExcludedCheck.Template.FindName("Track", ExcludedCheck) as Border
            ?? throw new InvalidOperationException("The Software exclusion toggle track is missing.");
        var thumb = ExcludedCheck.Template.FindName("Thumb", ExcludedCheck) as System.Windows.Shapes.Shape
            ?? throw new InvalidOperationException("The Software exclusion toggle thumb is missing.");
        var expectedFill = FindResource("ContentPrimaryBrush") as Brush
            ?? throw new InvalidOperationException("The Settings toggle active-state brush is missing.");
        if (ExcludedCheck.IsChecked != true ||
            ExcludedCheck.FocusVisualStyle is not null ||
            BrushColor(track.Background) != BrushColor(expectedFill) ||
            BrushColor(track.BorderBrush) != BrushColor(expectedFill) ||
            BrushColor(thumb.Fill) != Color.FromRgb(0x18, 0x18, 0x18))
        {
            throw new InvalidOperationException(
                "The enabled Software exclusion toggle does not match the bright Settings toggle state.");
        }
    }

    internal void VerifyTagColorStatesForPreview(Guid tagId)
    {
        var tag = TagsList.Items
            .OfType<TagDefinition>()
            .Single(item => item.Id == tagId);
        TagsList.UpdateLayout();
        var container = TagsList.ItemContainerGenerator.ContainerFromItem(tag) as ListBoxItem
            ?? throw new InvalidOperationException(
                "The Software settings tag chip was not generated.");
        container.ApplyTemplate();
        var chip = container.Template.FindName("Chip", container) as Border
            ?? throw new InvalidOperationException(
                "The Software settings tag-chip surface is missing.");
        var chipText = container.Template.FindName("ChipText", container) as TextBlock
            ?? throw new InvalidOperationException(
                "The Software settings tag-chip label is missing.");
        var expectedColor = ColorConverter.ConvertFromString(tag.Color) is Color parsed
            ? parsed
            : throw new InvalidOperationException("The Software tag has an invalid color.");

        container.IsSelected = false;
        container.UpdateLayout();
        if (BrushColor(chip.Background) == expectedColor ||
            BrushColor(chip.BorderBrush) == expectedColor ||
            BrushColor(chipText.Foreground) == expectedColor)
        {
            throw new InvalidOperationException(
                "An unused Software settings tag displayed its assigned color instead of gray.");
        }

        container.IsSelected = true;
        container.UpdateLayout();
        if (BrushColor(chip.Background) != expectedColor ||
            BrushColor(chip.BorderBrush) != expectedColor ||
            BrushColor(chipText.Foreground) != Colors.White)
        {
            throw new InvalidOperationException(
                "A selected Software settings tag did not display its assigned color.");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TryPrepareResult())
        {
            DialogResult = true;
        }
    }

    private bool TryPrepareResult()
    {
        if (string.IsNullOrWhiteSpace(LabelText.Text))
        {
            ValidationText.Text = "Enter a software label.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProcessText.Text))
        {
            ValidationText.Text = "Enter the process name, for example blender or blender.exe.";
            return false;
        }

        if (ProjectCombo.SelectedValue is not Guid projectId)
        {
            ValidationText.Text = "Choose a project scope.";
            return false;
        }

        if (ExcludedCheck.IsChecked != true && !CommitPendingTagText())
        {
            return false;
        }

        ProcessName = ProcessText.Text.Trim();
        Label = LabelText.Text.Trim();
        ProjectId = projectId;
        IsExcluded = ExcludedCheck.IsChecked == true;
        SelectedTagIds = TagsList.SelectedItems
            .OfType<TagDefinition>()
            .Select(tag => tag.Id)
            .ToArray();
        SelectedTagNames = TagsList.SelectedItems
            .OfType<TagDefinition>()
            .Select(tag => tag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    private void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateScopeHelpText();
        if (_tags is not null)
        {
            RefreshAvailableTags(
                TagsList.SelectedItems.OfType<TagDefinition>().Select(tag => tag.Id).ToHashSet());
        }
    }

    private void RefreshAvailableTags(IReadOnlySet<Guid> selectedIds)
    {
        if (ProjectCombo.SelectedValue is not Guid scopeId)
        {
            return;
        }

        var visible = scopeId == SystemEntityIds.GlobalSoftwareScopeId
            ? _allTags.Where(tag => tag.IsGlobal)
            : _allTags.Where(tag => tag.IsAvailableFor(scopeId));
        _tags.Clear();
        foreach (var tag in visible.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
        {
            _tags.Add(tag);
        }

        foreach (var tag in _tags.Where(tag => selectedIds.Contains(tag.Id)))
        {
            TagsList.SelectedItems.Add(tag);
        }

        NoTagsText.Visibility = _tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TagsList.Visibility = _tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateScopeHelpText()
    {
        if (ScopeHelpText is null)
        {
            return;
        }

        ScopeHelpText.Text = ProjectCombo.SelectedValue is Guid projectId &&
                             projectId == SystemEntityIds.GlobalSoftwareScopeId
            ? "No project: tracking, exclusion, and correlated tags are shared across every project."
            : "Tracking, exclusion, and correlated tags apply only to the selected project. The display label remains shared.";
    }

    private void ExcludedCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateCorrelatedTagsAvailability();
    }

    private void UpdateCorrelatedTagsAvailability()
    {
        var enabled = ExcludedCheck.IsChecked != true;
        CorrelatedTagsEditorPanel.IsEnabled = enabled;
        CorrelatedTagsListPanel.IsEnabled = enabled;
        CorrelatedTagsEditorPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CorrelatedTagsListPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            NewTagStatusText.Text = string.Empty;
        }
    }

    private void NewTagText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingNewTagText || !NewTagText.Text.Contains(','))
        {
            return;
        }

        var parts = NewTagText.Text.Split(',');
        foreach (var part in parts[..^1])
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                _ = CommitTagName(part);
            }
        }

        _updatingNewTagText = true;
        try
        {
            NewTagText.Text = parts[^1].TrimStart();
            NewTagText.CaretIndex = NewTagText.Text.Length;
        }
        finally
        {
            _updatingNewTagText = false;
        }
    }

    private bool CommitPendingTagText()
    {
        if (string.IsNullOrWhiteSpace(NewTagText.Text))
        {
            return true;
        }

        if (!CommitTagName(NewTagText.Text))
        {
            return false;
        }

        _updatingNewTagText = true;
        try
        {
            NewTagText.Clear();
        }
        finally
        {
            _updatingNewTagText = false;
        }

        return true;
    }

    private bool CommitTagName(string value)
    {
        var name = TagParser.Normalize(value);
        if (name is null)
        {
            NewTagStatusText.Foreground = (Brush)FindResource("DangerBrush");
            NewTagStatusText.Text = "Use letters, numbers, underscores, or hyphens in tag names.";
            return false;
        }

        var tag = _tags.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        var created = tag is null;
        if (created)
        {
            var scopeId = ProjectCombo.SelectedValue is Guid selectedScopeId
                ? selectedScopeId
                : SystemEntityIds.GlobalSoftwareScopeId;
            var isGlobal = scopeId == SystemEntityIds.GlobalSoftwareScopeId;
            tag = new TagDefinition(
                Guid.NewGuid(),
                name,
                GeneratePreviewTagColor(),
                isGlobal,
                isGlobal ? [] : [scopeId]);
            _allTags.Add(tag);
            _tags.Add(tag);
        }

        if (!TagsList.SelectedItems.Contains(tag))
        {
            TagsList.SelectedItems.Add(tag);
        }

        TagsList.Visibility = Visibility.Visible;
        NoTagsText.Visibility = Visibility.Collapsed;
        NewTagStatusText.Foreground = (Brush)FindResource("MutedBrush");
        NewTagStatusText.Text = created
            ? $"Added and selected #{name}."
            : $"Selected existing tag #{name}.";
        return true;
    }

    private string GeneratePreviewTagColor()
    {
        var existing = _tags
            .Select(tag => tag.Color)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var color = HsvToHex(
                Random.Shared.NextDouble() * 360d,
                0.58d + Random.Shared.NextDouble() * 0.24d,
                0.78d + Random.Shared.NextDouble() * 0.17d);
            if (!existing.Contains(color))
            {
                return color;
            }
        }

        throw new InvalidOperationException("Could not allocate a preview color for the new tag.");
    }

    private static string HsvToHex(double hueDegrees, double saturation, double value)
    {
        var sector = hueDegrees / 60d;
        var index = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return $"#{(byte)Math.Round(red * 255):X2}{(byte)Math.Round(green * 255):X2}{(byte)Math.Round(blue * 255):X2}";
    }

    private async void CaptureProcessButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CaptureActiveProcessAsync(TimeSpan.FromSeconds(3), minimizeWindow: true);
    }

    private async Task CaptureActiveProcessAsync(TimeSpan delay, bool minimizeWindow)
    {
        if (_captureCurrentActivity is null)
        {
            return;
        }

        CaptureProcessButton.IsEnabled = false;
        CaptureStatusText.Text = "Switch to the target application now. Capturing in 3 seconds…";
        if (minimizeWindow)
        {
            WindowState = WindowState.Minimized;
        }

        await Task.Delay(delay);
        var activity = _captureCurrentActivity();

        if (minimizeWindow)
        {
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        CaptureProcessButton.IsEnabled = true;
        if (activity is null || string.IsNullOrWhiteSpace(activity.ProcessName))
        {
            CaptureStatusText.Text = "No foreground process could be captured.";
            return;
        }

        ProcessText.Text = activity.ProcessName;
        if (string.IsNullOrWhiteSpace(LabelText.Text))
        {
            LabelText.Text = activity.ProcessName;
        }

        CaptureStatusText.Text = $"Captured {activity.ProcessName}. You can edit its display label before saving.";
        LabelText.Focus();
        LabelText.SelectAll();
    }

    private static Color? BrushColor(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color : null;
}
