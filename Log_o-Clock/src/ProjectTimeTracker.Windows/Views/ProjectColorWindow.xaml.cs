using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class ProjectColorWindow : Window
{
    private const int WheelSize = 220;
    private const double Radius = WheelSize / 2d - 2;
    private double _hue;
    private double _saturation;
    private double _brightness = 1;
    private bool _ready;

    public ProjectColorWindow(Project project, string clientName)
        : this("Project color", $"{clientName} / {project.Name}", project.Color)
    {
    }

    public ProjectColorWindow(string title, string displayName, string initialColor)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = displayName;
        var initial = (Color)ColorConverter.ConvertFromString(initialColor);
        RgbToHsv(initial.R, initial.G, initial.B, out _hue, out _saturation, out _brightness);
        BrightnessSlider.Value = Math.Clamp(_brightness, BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        Loaded += (_, _) =>
        {
            WheelImage.Source = CreateColorWheel();
            _ready = true;
            UpdatePreview();
        };
    }

    public string SelectedColorHex { get; private set; } = "#FF7356";

    internal void VerifyWheelInteractionForPreview()
    {
        UpdateLayout();
        var testPoint = new Point(WheelSize - 12, WheelSize / 2d);
        if (WheelSurface.InputHitTest(testPoint) is null)
        {
            throw new InvalidOperationException("The color wheel does not expose a hit-test surface.");
        }

        var previousColor = SelectedColorHex;
        PickWheelColor(new Point(WheelSize / 2d, 12));
        if (string.Equals(previousColor, SelectedColorHex, StringComparison.OrdinalIgnoreCase) ||
            double.IsNaN(Canvas.GetLeft(WheelSelector)) ||
            double.IsNaN(Canvas.GetTop(WheelSelector)))
        {
            throw new InvalidOperationException("The color wheel did not update its selection and preview.");
        }
    }

    private static WriteableBitmap CreateColorWheel()
    {
        var pixels = new byte[WheelSize * WheelSize * 4];
        var center = (WheelSize - 1) / 2d;
        for (var y = 0; y < WheelSize; y++)
        {
            for (var x = 0; x < WheelSize; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var saturation = Math.Sqrt(dx * dx + dy * dy) / Radius;
                if (saturation > 1)
                {
                    continue;
                }

                var hue = Math.Atan2(dy, dx) / (Math.PI * 2);
                if (hue < 0)
                {
                    hue += 1;
                }

                var color = HsvToColor(hue, saturation, 1);
                var index = (y * WheelSize + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, WheelSize * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private void Wheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        WheelSurface.CaptureMouse();
        PickWheelColor(e.GetPosition(WheelSurface));
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        _ = sender;
        if (e.LeftButton == MouseButtonState.Pressed && WheelSurface.IsMouseCaptured)
        {
            PickWheelColor(e.GetPosition(WheelSurface));
        }
    }

    private void Wheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        PickWheelColor(e.GetPosition(WheelSurface));
        WheelSurface.ReleaseMouseCapture();
    }

    private void PickWheelColor(Point point)
    {
        var center = WheelSize / 2d;
        var dx = point.X - center;
        var dy = point.Y - center;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        _saturation = Math.Clamp(distance / Radius, 0, 1);
        _hue = Math.Atan2(dy, dx) / (Math.PI * 2);
        if (_hue < 0)
        {
            _hue += 1;
        }

        UpdatePreview();
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ = sender;
        _brightness = e.NewValue;
        if (_ready)
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var color = HsvToColor(_hue, _saturation, _brightness);
        SelectedColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        PreviewSwatch.Background = new SolidColorBrush(color);
        HexText.Text = SelectedColorHex;

        var angle = _hue * Math.PI * 2;
        var distance = _saturation * Radius;
        Canvas.SetLeft(WheelSelector, WheelSize / 2d + Math.Cos(angle) * distance - WheelSelector.Width / 2);
        Canvas.SetTop(WheelSelector, WheelSize / 2d + Math.Sin(angle) * distance - WheelSelector.Height / 2);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = true;
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        var sector = hue * 6;
        var index = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (r, g, b) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static void RgbToHsv(byte red, byte green, byte blue, out double hue, out double saturation, out double value)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        value = max;
        saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0)
        {
            hue = 0;
            return;
        }

        hue = max == r
            ? ((g - b) / delta) % 6
            : max == g
                ? (b - r) / delta + 2
                : (r - g) / delta + 4;
        hue /= 6;
        if (hue < 0)
        {
            hue += 1;
        }
    }
}
