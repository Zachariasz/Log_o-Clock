using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    internal void VerifyConnectionModeChoicesForPreview()
    {
        var sharedStyle = TryFindResource(typeof(RadioButton)) as Style;
        if (sharedStyle is null ||
            !ReferenceEquals(CreateNewRadio.Style, sharedStyle) ||
            !ReferenceEquals(UseExistingRadio.Style, sharedStyle))
        {
            throw new InvalidOperationException("Google Sheets connection choices must use the shared dark radio-button style.");
        }
    }

    private void ConnectionMode_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ExistingSpreadsheetPanel is null)
        {
            return;
        }

        ExistingSpreadsheetPanel.Visibility = UseExistingRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
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

        var useExisting = UseExistingRadio.IsChecked == true;
        var spreadsheet = SpreadsheetText.Text.Trim();
        if (useExisting && spreadsheet.Length == 0)
        {
            ValidationText.Text = "Paste the shared Google Sheets URL or spreadsheet ID.";
            SpreadsheetText.Focus();
            return;
        }

        ConnectButton.IsEnabled = false;
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("ContentSecondaryBrush");
        ValidationText.Text = "Waiting for authorization in your browser…";
        try
        {
            _ = useExisting
                ? await _syncService.ConnectExistingAsync(clientId, clientSecret, spreadsheet)
                : await _syncService.ConnectAsync(clientId, clientSecret);
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
