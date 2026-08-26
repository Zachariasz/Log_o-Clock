using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ProjectTimeTracker.Core;
using MessageBox = ProjectTimeTracker.Windows.Views.ThemedMessageBox;

namespace ProjectTimeTracker.Windows.Views;

public partial class SyncConflictReviewWindow : Window
{
    private readonly IGoogleSheetsSyncService _syncService;
    private IReadOnlyList<ConflictItem> _items = [];

    public SyncConflictReviewWindow(IGoogleSheetsSyncService syncService)
    {
        _syncService = syncService;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var selectedId = (ConflictList.SelectedItem as ConflictItem)?.Conflict.Id;
        _items = (await _syncService.GetConflictsAsync())
            .Select(conflict => new ConflictItem(conflict, BuildConflictLabel(conflict)))
            .ToArray();
        ConflictList.ItemsSource = _items;
        ConflictList.SelectedItem = _items.FirstOrDefault(item => item.Conflict.Id == selectedId)
                                    ?? _items.FirstOrDefault();
        IntroText.Text = _items.Count == 0
            ? "All conflicts are resolved. Synchronization can converge normally."
            : $"{_items.Count} unresolved conflict{(_items.Count == 1 ? string.Empty : "s")}. Unrelated records continue synchronizing.";
        if (_items.Count == 0)
        {
            ShowConflict(null);
        }
    }

    private void ConflictList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowConflict((ConflictList.SelectedItem as ConflictItem)?.Conflict);
    }

    private void ShowConflict(ProfileSyncConflict? conflict)
    {
        var hasConflict = conflict is not null;
        ConflictHeadingText.Text = conflict is null ? "No unresolved conflicts" : $"{FriendlyType(conflict.EntityType)} conflict";
        ConflictSummaryText.Text = conflict?.Summary ?? "There is nothing to review.";
        CloudVersionCombo.ItemsSource = conflict?.Heads
            .Where(head => head.Operation == ProfileSyncOperation.Upsert)
            .Select(head => new VersionItem(head, BuildVersionLabel(head)))
            .ToArray() ?? [];
        CloudVersionCombo.SelectedIndex = CloudVersionCombo.Items.Count > 0 ? 0 : -1;
        AffectedItemsText.Text = string.IsNullOrWhiteSpace(conflict?.RelatedEntityIdsJson)
            ? string.Empty
            : $"Affected records: {FormatRelated(conflict.RelatedEntityIdsJson)}";
        var isLegacy = conflict?.Kind == ProfileSyncConflictKind.LegacyEntry;
        var isDeleteVersusEdit = conflict?.Kind == ProfileSyncConflictKind.DeleteVersusEdit;
        var isEditableFork = hasConflict && !isLegacy && !isDeleteVersusEdit;
        CloudVersionLabel.Visibility = isEditableFork ? Visibility.Visible : Visibility.Collapsed;
        CloudVersionCombo.Visibility = isEditableFork ? Visibility.Visible : Visibility.Collapsed;
        KeepLocalButton.Visibility = isEditableFork ? Visibility.Visible : Visibility.Collapsed;
        KeepCloudButton.Visibility = isEditableFork ? Visibility.Visible : Visibility.Collapsed;
        KeepLocalButton.IsEnabled = isEditableFork;
        KeepCloudButton.IsEnabled = isEditableFork && CloudVersionCombo.Items.Count > 0;
        KeepBothButton.Visibility = isEditableFork && conflict?.EntityType == "TimeEntries" ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Visibility = isDeleteVersusEdit ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = isDeleteVersusEdit ? Visibility.Visible : Visibility.Collapsed;
        ImportLegacyButton.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;
        IgnoreLegacyButton.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;
        ActionStatusText.Text = string.Empty;
    }

    private async Task ResolveAsync(ProfileSyncResolution resolution, bool destructive = false)
    {
        if (ConflictList.SelectedItem is not ConflictItem selected)
        {
            return;
        }
        if (destructive && MessageBox.Show(
                this,
                "Delete the selected shared record and all listed dependent work? This decision will synchronize to every connected computer.",
                "Confirm shared deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var cloudRevision = (CloudVersionCombo.SelectedItem as VersionItem)?.Change.RevisionId;
        try
        {
            SetActionsEnabled(false);
            await _syncService.ResolveConflictAsync(selected.Conflict.Id, resolution, cloudRevision);
            await _syncService.SyncNowAsync();
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        KeepLocalButton.IsEnabled = enabled;
        KeepCloudButton.IsEnabled = enabled && CloudVersionCombo.Items.Count > 0;
        KeepBothButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
        ImportLegacyButton.IsEnabled = enabled;
        IgnoreLegacyButton.IsEnabled = enabled;
    }

    private async void KeepLocal_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.KeepLocal); }
    private async void KeepCloud_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.KeepCloud); }
    private async void KeepBoth_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.KeepBoth); }
    private async void Restore_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.Restore); }
    private async void Delete_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.Delete, destructive: true); }
    private async void ImportLegacy_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.ImportLegacy); }
    private async void IgnoreLegacy_Click(object sender, RoutedEventArgs e) { _ = sender; _ = e; await ResolveAsync(ProfileSyncResolution.IgnoreLegacy); }

    private static string BuildConflictLabel(ProfileSyncConflict conflict) =>
        $"{FriendlyType(conflict.EntityType)} · {FriendlyConflictKind(conflict.Kind)} · {conflict.DetectedUtc.ToLocalTime():g}";

    private static string BuildVersionLabel(ProfileSyncChange change) =>
        $"{change.DeviceName} · {change.ChangedUtc.ToLocalTime():g} · {PayloadHint(change.PayloadJson)}";

    private static string PayloadHint(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "deleted";
        }
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var key in new[] { "Name", "Description", "ProcessName", "StartUtc" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Length <= 70 ? text : text[..67] + "…";
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return "record data";
    }

    private static string FriendlyType(string value) => value switch
    {
        "TimeEntries" => "Log entry",
        "SavedTasks" => "Task",
        "RecognitionRules" => "Recognition rule",
        "LegacyEntry" => "Legacy log",
        _ => value,
    };

    private static string FriendlyConflictKind(ProfileSyncConflictKind kind) => kind switch
    {
        ProfileSyncConflictKind.ConcurrentEdit => "Concurrent changes",
        ProfileSyncConflictKind.DeleteVersusEdit => "Delete versus edit",
        ProfileSyncConflictKind.IdentityCollision => "Matching identity",
        ProfileSyncConflictKind.InvalidRemoteRecord => "Invalid cloud record",
        ProfileSyncConflictKind.LegacyEntry => "Import review",
        _ => kind.ToString(),
    };

    private static string FormatRelated(string json)
    {
        try
        {
            return string.Join(", ", JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private sealed record ConflictItem(ProfileSyncConflict Conflict, string Label);
    private sealed record VersionItem(ProfileSyncChange Change, string Label);
}
