using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ProjectTimeTracker.Windows.Controls;

internal enum TargetProgressPeriod
{
    Daily,
    Weekly,
    Monthly,
}

public sealed class TargetProgressRing : FrameworkElement
{
    private const double DefaultSize = 40d;

    public static readonly DependencyProperty DailyProgressProperty = DependencyProperty.Register(
        nameof(DailyProgress),
        typeof(double),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WeeklyProgressProperty = DependencyProperty.Register(
        nameof(WeeklyProgress),
        typeof(double),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MonthlyProgressProperty = DependencyProperty.Register(
        nameof(MonthlyProgress),
        typeof(double),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DailyBrushProperty = DependencyProperty.Register(
        nameof(DailyBrush),
        typeof(Brush),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WeeklyBrushProperty = DependencyProperty.Register(
        nameof(WeeklyBrush),
        typeof(Brush),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MonthlyBrushProperty = DependencyProperty.Register(
        nameof(MonthlyBrush),
        typeof(Brush),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(TargetProgressRing),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double DailyProgress
    {
        get => (double)GetValue(DailyProgressProperty);
        set => SetValue(DailyProgressProperty, value);
    }

    public double WeeklyProgress
    {
        get => (double)GetValue(WeeklyProgressProperty);
        set => SetValue(WeeklyProgressProperty, value);
    }

    public double MonthlyProgress
    {
        get => (double)GetValue(MonthlyProgressProperty);
        set => SetValue(MonthlyProgressProperty, value);
    }

    public Brush DailyBrush
    {
        get => (Brush)GetValue(DailyBrushProperty);
        set => SetValue(DailyBrushProperty, value);
    }

    public Brush WeeklyBrush
    {
        get => (Brush)GetValue(WeeklyBrushProperty);
        set => SetValue(WeeklyBrushProperty, value);
    }

    public Brush MonthlyBrush
    {
        get => (Brush)GetValue(MonthlyBrushProperty);
        set => SetValue(MonthlyBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    internal IReadOnlyList<TargetProgressPeriod> OrderedPeriodsForPreview =>
        GetOrderedSegments().Select(segment => segment.Period).ToArray();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? DefaultSize
            : Math.Min(DefaultSize, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height)
            ? DefaultSize
            : Math.Min(DefaultSize, availableSize.Height);
        return new Size(Math.Max(0d, width), Math.Max(0d, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0d)
        {
            return;
        }

        var thickness = Math.Clamp(StrokeThickness, 1d, Math.Max(1d, size / 2d));
        var radius = Math.Max(0d, (size - thickness) / 2d);
        if (radius <= 0d)
        {
            return;
        }

        var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
        drawingContext.DrawEllipse(
            null,
            CreatePen(TrackBrush, thickness),
            center,
            radius,
            radius);

        // Longer arcs are drawn first. Every shorter arc is consequently on top
        // of the shared ring and remains visible instead of being covered.
        foreach (var segment in GetOrderedSegments())
        {
            DrawProgressArc(
                drawingContext,
                center,
                radius,
                thickness,
                segment.Progress,
                segment.Brush);
        }
    }

    private IReadOnlyList<TargetProgressSegment> GetOrderedSegments() =>
        new[]
            {
                CreateSegment(TargetProgressPeriod.Daily, DailyProgress, DailyBrush),
                CreateSegment(TargetProgressPeriod.Weekly, WeeklyProgress, WeeklyBrush),
                CreateSegment(TargetProgressPeriod.Monthly, MonthlyProgress, MonthlyBrush),
            }
            .Where(segment => segment.Progress >= 0d)
            .OrderByDescending(segment => segment.Progress)
            .ToArray();

    private static TargetProgressSegment CreateSegment(
        TargetProgressPeriod period,
        double progress,
        Brush brush) =>
        new(
            period,
            double.IsFinite(progress) ? Math.Clamp(progress, 0d, 1d) : -1d,
            brush);

    private static void DrawProgressArc(
        DrawingContext drawingContext,
        Point center,
        double radius,
        double thickness,
        double progress,
        Brush brush)
    {
        if (progress <= 0d)
        {
            return;
        }

        var pen = CreatePen(brush, thickness);
        if (progress >= 0.999999d)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        const double startAngle = -90d;
        var endAngle = startAngle + progress * 360d;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0d,
                isLargeArc: progress > 0.5d,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Pen CreatePen(Brush brush, double thickness) =>
        new(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(angleRadians),
            center.Y + radius * Math.Sin(angleRadians));
    }

    private sealed record TargetProgressSegment(
        TargetProgressPeriod Period,
        double Progress,
        Brush Brush);
}
