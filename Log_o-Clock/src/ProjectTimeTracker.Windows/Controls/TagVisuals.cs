using System;
using System.Collections.Generic;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Controls;

internal static class TagVisuals
{
    public static IReadOnlyDictionary<string, Brush> CreateBrushes(IEnumerable<TagDefinition>? tags)
    {
        var brushes = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        if (tags is null)
        {
            return brushes;
        }

        foreach (var tag in tags)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tag.Color));
                brush.Freeze();
                brushes[tag.Name] = brush;
            }
            catch (FormatException)
            {
                brushes[tag.Name] = FallbackBrush(tag.Name);
            }
        }

        return brushes;
    }

    public static Brush Resolve(IReadOnlyDictionary<string, Brush> brushes, string name) =>
        brushes.TryGetValue(name, out var brush) ? brush : FallbackBrush(name);

    private static Brush FallbackBrush(string name)
    {
        uint hash = 2166136261;
        foreach (var character in name.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 16777619;
        }

        var hue = hash % 360;
        var color = HsvToColor(hue, 0.68, 0.9);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color HsvToColor(double hueDegrees, double saturation, double value)
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
        return Color.FromRgb(
            (byte)Math.Round(red * 255),
            (byte)Math.Round(green * 255),
            (byte)Math.Round(blue * 255));
    }
}
