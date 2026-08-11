using System.Diagnostics;
using System.Windows;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class TrelloConnectionWindow : Window
{
    private readonly ITrelloSyncService _syncService;

    public TrelloConnectionWindow(ITrelloSyncService syncService)
    {
        _syncService = syncService;
        InitializeComponent();
        Loaded += (_, _) => ApiKeyText.Focus();
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenBrowser(new Uri("https://trello.com/power-ups/admin"));
    }

    private void Authorize_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var key = ApiKeyText.Text.Trim();
        if (key.Length == 0)
        {
            ValidationText.Text = "Paste your Trello API key first.";
            ApiKeyText.Focus();
            return;
        }

        ValidationText.Text = string.Empty;
        OpenBrowser(_syncService.CreateAuthorizationUri(key));
        TokenText.Focus();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var key = ApiKeyText.Text.Trim();
        var token = TokenText.Password.Trim();
        if (key.Length == 0 || token.Length == 0)
        {
            ValidationText.Text = "Paste both the API key and read-only token.";
            return;
        }

        ConnectButton.IsEnabled = false;
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("ContentSecondaryBrush");
        ValidationText.Text = "Validating Trello account…";
        try
        {
            _ = await _syncService.ConnectAsync(key, token);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            ValidationText.Text = exception.Message;
            ConnectButton.IsEnabled = true;
        }
    }

    private static void OpenBrowser(Uri uri)
    {
        _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The browser could not be opened.");
    }
}
