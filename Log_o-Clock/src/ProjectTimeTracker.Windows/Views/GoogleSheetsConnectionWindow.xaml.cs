using System.Diagnostics;
using System.Windows;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class GoogleSheetsConnectionWindow : Window
{
    private readonly IGoogleSheetsSyncService _syncService;

    public GoogleSheetsConnectionWindow(IGoogleSheetsSyncService syncService)
    {
        _syncService = syncService;
        InitializeComponent();
        Loaded += (_, _) => ClientIdText.Focus();
    }

    private void OpenGoogleCloud_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials")
        {
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("The browser could not be opened.");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var clientId = ClientIdText.Text.Trim();
        var clientSecret = ClientSecretText.Password.Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            ValidationText.Text = "Paste both values from your Google Desktop OAuth client.";
            return;
        }

        ConnectButton.IsEnabled = false;
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("ContentSecondaryBrush");
        ValidationText.Text = "Waiting for authorization in your browserâ€¦";
        try
        {
            _ = await _syncService.ConnectAsync(clientId, clientSecret);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            ValidationText.Text = exception.Message;
            ConnectButton.IsEnabled = true;
        }
    }
}
