using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class TrelloMappingWindow : Window
{
    private readonly ITrelloSyncService _syncService;
    private readonly TrelloBoardMapping? _existing;
    private bool _loading;

    public TrelloMappingWindow(
        ITrelloSyncService syncService,
        IReadOnlyList<ProjectOption> projects,
        TrelloBoardMapping? existing = null)
    {
        _syncService = syncService;
        _existing = existing;
        InitializeComponent();
        HeadingText.Text = existing is null ? "Add Trello mapping" : "Edit Trello mapping";
        Title = HeadingText.Text;
        ProjectCombo.ItemsSource = projects;
        ProjectCombo.SelectedValue = existing?.ProjectId ?? projects.FirstOrDefault()?.ProjectId;
        Loaded += OnLoaded;
    }

    public TrelloBoardMapping? Result { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await LoadBoardsAsync();
    }

    private async Task LoadBoardsAsync()
    {
        SetBusy(true, "Loading Trello boards…");
        try
        {
            var boards = (await _syncService.GetBoardsAsync()).ToList();
            if (_existing is not null && boards.All(board => board.Id != _existing.BoardId))
            {
                boards.Add(new TrelloBoard(_existing.BoardId, _existing.BoardName, string.Empty));
            }

            _loading = true;
            BoardCombo.ItemsSource = boards.OrderBy(board => board.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            BoardCombo.SelectedValue = _existing?.BoardId ?? boards.FirstOrDefault()?.Id;
            _loading = false;
            if (BoardCombo.SelectedItem is TrelloBoard selected)
            {
                await LoadListsAsync(selected.Id);
            }
            else
            {
                SetBusy(false, "No open Trello boards are available.", error: true);
            }
        }
        catch (Exception exception)
        {
            _loading = false;
            SetBusy(false, exception.Message, error: true);
        }
    }

    private async void BoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loading && BoardCombo.SelectedItem is TrelloBoard board)
        {
            await LoadListsAsync(board.Id);
        }
    }

    private async Task LoadListsAsync(string boardId)
    {
        SetBusy(true, "Loading board lists…");
        try
        {
            var selectedIds = _existing is not null && _existing.BoardId == boardId
                ? _existing.Lists.Select(list => list.ListId).ToHashSet(StringComparer.Ordinal)
                : [];
            ListsBox.ItemsSource = (await _syncService.GetListsAsync(boardId))
                .Select(list => new TrelloListChoice(list, selectedIds.Contains(list.Id)))
                .ToArray();
            SetBusy(false, string.Empty);
        }
        catch (Exception exception)
        {
            ListsBox.ItemsSource = null;
            SetBusy(false, exception.Message, error: true);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectCombo.SelectedValue is not Guid projectId || BoardCombo.SelectedItem is not TrelloBoard board)
        {
            ValidationText.Text = "Choose a local project and Trello board.";
            return;
        }

        var lists = ListsBox.Items.OfType<TrelloListChoice>()
            .Where(choice => choice.IsSelected)
            .Select(choice => new TrelloListMapping(choice.List.Id, choice.List.Name))
            .ToArray();
        if (lists.Length == 0)
        {
            ValidationText.Text = "Choose at least one Trello list.";
            return;
        }

        Result = new TrelloBoardMapping(
            _existing?.Id ?? Guid.NewGuid(),
            projectId,
            board.Id,
            board.Name,
            lists);
        DialogResult = true;
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        SaveButton.IsEnabled = !busy;
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "DangerBrush" : "ContentSecondaryBrush");
        ValidationText.Text = message;
    }

    private sealed class TrelloListChoice(TrelloList list, bool isSelected) : INotifyPropertyChanged
    {
        private bool _isSelected = isSelected;
        public TrelloList List { get; } = list;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
