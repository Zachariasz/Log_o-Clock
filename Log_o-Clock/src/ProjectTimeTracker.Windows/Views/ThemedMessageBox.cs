using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ProjectTimeTracker.Windows.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace ProjectTimeTracker.Windows.Views;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image) => Show(FindOwner(), message, caption, buttons, image);

    public static MessageBoxResult Show(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult) => Show(FindOwner(), message, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
        => Show(owner, message, caption, buttons, image, MessageBoxResult.None);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
        => Show(owner, message, caption, buttons, image, defaultResult, topmost: false);

    public static MessageBoxResult ShowTopmost(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
        => Show(FindOwner(), message, caption, buttons, image, defaultResult, topmost: true);

    private static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult,
        bool topmost)
    {
        var dialog = CreateDialog(message, caption, buttons, image, defaultResult, topmost);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = dialog.ShowDialog();
        return dialog.Result;
    }

    internal static Window CreateTopmostTimeReviewForPreview() =>
        CreateDialog(
            "You were away for 00:05:00.\n\nRemove this interval from the running timer?",
            "Review idle time",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes,
            topmost: true);

    private static MessageDialogWindow CreateDialog(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult,
        bool topmost) =>
        new(message, caption, buttons, image, defaultResult)
        {
            Topmost = topmost,
        };

    private static Window? FindOwner() => Application.Current?.Windows
        .OfType<Window>()
        .FirstOrDefault(window => window.IsActive)
        ?? Application.Current?.MainWindow;

    private sealed class MessageDialogWindow : Window
    {
        private readonly MessageBoxButton _buttons;
        private readonly MessageBoxResult _defaultResult;

        public MessageDialogWindow(
            string message,
            string caption,
            MessageBoxButton buttons,
            MessageBoxImage image,
            MessageBoxResult defaultResult)
        {
            _buttons = buttons;
            _defaultResult = defaultResult;
            Title = caption;
            Width = 460;
            MinHeight = 210;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            Style = Resource<Style>("DialogWindowStyle");
            Background = Resource<Brush>("SurfaceElevatedBrush");
            Foreground = Resource<Brush>("ContentPrimaryBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            WindowBackdropService.SetRole(this, WindowBackdropRole.Dialog);

            Content = BuildContent(message, caption, image);
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, _) =>
            {
                if (Topmost)
                {
                    _ = Activate();
                }

                _ = MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            };
        }

        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private UIElement BuildContent(string message, string caption, MessageBoxImage image)
        {
            var border = new Border
            {
                Background = Resource<Brush>("SurfaceElevatedBrush"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(24),
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.Children.Add(CreateIcon(image));
            var title = new TextBlock
            {
                Text = caption,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(title, 1);
            heading.Children.Add(title);
            root.Children.Add(heading);

            var body = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Resource<Brush>("ContentSecondaryBrush"),
                FontSize = 14,
                LineHeight = 20,
                Margin = new Thickness(40, 16, 0, 24),
            };
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            AddButtons(actions);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);
            border.Child = root;
            return border;
        }

        private ShapePath CreateIcon(MessageBoxImage image)
        {
            var (key, brushKey) = image switch
            {
                MessageBoxImage.Error => ("Icon.Warning", "DangerBrush"),
                MessageBoxImage.Warning => ("Icon.Warning", "WarningBrush"),
                MessageBoxImage.Question => ("Icon.Info", "FocusBrush"),
                _ => ("Icon.Info", "ContentSecondaryBrush"),
            };
            return new ShapePath
            {
                Data = Resource<Geometry>(key),
                Stroke = Resource<Brush>(brushKey),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private void AddButtons(Panel actions)
        {
            switch (_buttons)
            {
                case MessageBoxButton.OK:
                    actions.Children.Add(CreateButton("OK", MessageBoxResult.OK, primary: true, isDefault: IsDefault(MessageBoxResult.OK)));
                    break;
                case MessageBoxButton.OKCancel:
                    actions.Children.Add(CreateButton("Cancel", MessageBoxResult.Cancel, primary: false, isCancel: true));
                    actions.Children.Add(CreateButton("OK", MessageBoxResult.OK, primary: true, isDefault: IsDefault(MessageBoxResult.OK)));
                    break;
                case MessageBoxButton.YesNo:
                    actions.Children.Add(CreateButton("No", MessageBoxResult.No, primary: false, isDefault: IsDefault(MessageBoxResult.No), isCancel: true));
                    actions.Children.Add(CreateButton("Yes", MessageBoxResult.Yes, primary: true, isDefault: IsDefault(MessageBoxResult.Yes)));
                    break;
                case MessageBoxButton.YesNoCancel:
                    actions.Children.Add(CreateButton("Cancel", MessageBoxResult.Cancel, primary: false, isCancel: true));
                    actions.Children.Add(CreateButton("No", MessageBoxResult.No, primary: false, isDefault: IsDefault(MessageBoxResult.No)));
                    actions.Children.Add(CreateButton("Yes", MessageBoxResult.Yes, primary: true, isDefault: IsDefault(MessageBoxResult.Yes)));
                    break;
            }
        }

        private bool IsDefault(MessageBoxResult result) =>
            _defaultResult == result ||
            _defaultResult == MessageBoxResult.None && result is MessageBoxResult.OK or MessageBoxResult.Yes;

        private Button CreateButton(string text, MessageBoxResult result, bool primary, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 88,
                Margin = new Thickness(4, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel,
                Style = Resource<Style>(primary ? "PrimaryButton" : "SecondaryButton"),
            };
            button.Click += (_, _) => CloseWith(result);
            return button;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            _ = sender;
            if (e.Key == Key.Escape)
            {
                CloseWith(_buttons is MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel);
                e.Handled = true;
            }
        }

        private void CloseWith(MessageBoxResult result)
        {
            Result = result;
            DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        }

        private static T Resource<T>(string key) where T : class =>
            (Application.Current.TryFindResource(key) as T)
            ?? throw new InvalidOperationException($"Missing UI resource '{key}'.");
    }
}
