using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ProjectTimeTracker.Windows.Services;

/// <summary>
/// Converts WPF's row-sized mouse-wheel jumps into short, pixel-based motion
/// and bridges Windows horizontal wheel/touchpad messages that WPF does not
/// expose as a routed event. The nearest scrollable viewer wins so nested
/// lists hand either axis back to their parent naturally at an edge.
/// </summary>
public static class SmoothScrollBehavior
{
    private const int WmMouseHorizontalWheel = 0x020E;
    private const double PixelsPerWheelNotch = 72d;
    private const double AnimationSeconds = 0.15d;
    private const double BoundaryTolerance = 0.5d;

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> VerticalAnimations = new();
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> HorizontalAnimations = new();
    private static readonly ConditionalWeakTable<HwndSource, HorizontalWheelHook> HorizontalWheelHooks = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
            element.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded));
            element.AddHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnElementUnloaded));
            if (element is FrameworkElement { IsLoaded: true })
            {
                EnsureHorizontalWheelHook(element);
            }
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
            element.RemoveHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded));
            element.RemoveHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnElementUnloaded));
            StopOwnedAnimation(element);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || args.Delta == 0 || sender is not DependencyObject owner)
        {
            return;
        }

        var source = args.OriginalSource as DependencyObject ?? owner;
        var viewer = FindVerticalScrollableViewer(source, args.Delta) ??
                     FindVerticalScrollableDescendant(owner, args.Delta);
        if (viewer is null)
        {
            return;
        }

        var pixelDelta = -(args.Delta / 120d) * PixelsPerWheelNotch;
        if (SystemParameters.ClientAreaAnimation)
        {
            VerticalAnimations.GetValue(
                    viewer,
                    static scrollViewer => new ScrollAnimation(scrollViewer, ScrollAxis.Vertical))
                .AddDelta(pixelDelta);
        }
        else
        {
            viewer.ScrollToVerticalOffset(Clamp(viewer.VerticalOffset + pixelDelta, viewer.ScrollableHeight));
        }

        args.Handled = true;
    }

    private static ScrollViewer? FindVerticalScrollableViewer(DependencyObject source, int wheelDelta)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is not ScrollViewer viewer || viewer.ScrollableHeight <= BoundaryTolerance)
            {
                continue;
            }

            var canMoveUp = wheelDelta > 0 && viewer.VerticalOffset > BoundaryTolerance;
            var canMoveDown = wheelDelta < 0 && viewer.VerticalOffset < viewer.ScrollableHeight - BoundaryTolerance;
            if (canMoveUp || canMoveDown)
            {
                return viewer;
            }
        }

        return null;
    }

    private static ScrollViewer? FindVerticalScrollableDescendant(DependencyObject owner, int wheelDelta)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(owner);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!ReferenceEquals(current, owner) &&
                current is ScrollViewer viewer &&
                CanMoveVertically(viewer, wheelDelta))
            {
                return viewer;
            }

            var childCount = current is Visual or Visual3D
                ? VisualTreeHelper.GetChildrenCount(current)
                : 0;
            for (var index = 0; index < childCount; index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }

    private static bool CanMoveVertically(ScrollViewer viewer, int wheelDelta)
    {
        if (viewer.ScrollableHeight <= BoundaryTolerance)
        {
            return false;
        }

        return wheelDelta > 0
            ? viewer.VerticalOffset > BoundaryTolerance
            : viewer.VerticalOffset < viewer.ScrollableHeight - BoundaryTolerance;
    }

    private static ScrollViewer? FindHorizontalScrollableViewer(DependencyObject source, int wheelDelta)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is not ScrollViewer viewer ||
                !CanMoveHorizontally(viewer, wheelDelta) ||
                !IsSmoothScrollingEnabled(viewer))
            {
                continue;
            }

            return viewer;
        }

        return null;
    }

    private static bool CanMoveHorizontally(ScrollViewer viewer, int wheelDelta)
    {
        if (viewer.ScrollableWidth <= BoundaryTolerance)
        {
            return false;
        }

        return wheelDelta < 0
            ? viewer.HorizontalOffset > BoundaryTolerance
            : viewer.HorizontalOffset < viewer.ScrollableWidth - BoundaryTolerance;
    }

    private static bool IsSmoothScrollingEnabled(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (GetIsEnabled(current))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(element);
        }

        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return LogicalTreeHelper.GetParent(element);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs args)
    {
        _ = args;
        if (sender is UIElement element)
        {
            EnsureHorizontalWheelHook(element);
        }
    }

    private static void EnsureHorizontalWheelHook(UIElement element)
    {
        if (PresentationSource.FromVisual(element) is HwndSource source)
        {
            HorizontalWheelHooks
                .GetValue(source, static hwndSource => new HorizontalWheelHook(hwndSource))
                .Attach();
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is UIElement element)
        {
            StopOwnedAnimation(element);
        }
    }

    private static void StopOwnedAnimation(UIElement element)
    {
        if (element is not ScrollViewer viewer)
        {
            return;
        }

        if (VerticalAnimations.TryGetValue(viewer, out var verticalAnimation))
        {
            verticalAnimation.Stop();
            VerticalAnimations.Remove(viewer);
        }

        if (HorizontalAnimations.TryGetValue(viewer, out var horizontalAnimation))
        {
            horizontalAnimation.Stop();
            HorizontalAnimations.Remove(viewer);
        }
    }

    internal static bool ScrollHorizontalForPreview(
        ScrollViewer viewer,
        int wheelDelta,
        bool animate = false)
    {
        if (wheelDelta == 0 || !CanMoveHorizontally(viewer, wheelDelta))
        {
            return false;
        }

        ApplyHorizontalWheel(viewer, wheelDelta, animate);
        return true;
    }

    internal static int GetHorizontalWheelDeltaForPreview(nint wParam) =>
        GetWheelDelta(wParam);

    internal static bool HasHorizontalWheelHookForPreview(Visual visual) =>
        PresentationSource.FromVisual(visual) is HwndSource source &&
        HorizontalWheelHooks.TryGetValue(source, out var hook) &&
        hook.IsAttached;

    private static void ApplyHorizontalWheel(ScrollViewer viewer, int wheelDelta, bool animate)
    {
        var pixelDelta = (wheelDelta / 120d) * PixelsPerWheelNotch;
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            HorizontalAnimations.GetValue(
                    viewer,
                    static scrollViewer => new ScrollAnimation(scrollViewer, ScrollAxis.Horizontal))
                .AddDelta(pixelDelta);
        }
        else
        {
            viewer.ScrollToHorizontalOffset(
                Clamp(viewer.HorizontalOffset + pixelDelta, viewer.ScrollableWidth));
        }
    }

    private static int GetWheelDelta(nint wParam) =>
        unchecked((short)(((long)wParam >> 16) & 0xFFFF));

    private static Point GetScreenPoint(nint lParam)
    {
        var value = (long)lParam;
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private static double Clamp(double value, double maximum) =>
        Math.Max(0d, Math.Min(value, Math.Max(0d, maximum)));

    private enum ScrollAxis
    {
        Horizontal,
        Vertical,
    }

    private sealed class ScrollAnimation
    {
        private readonly ScrollViewer _viewer;
        private readonly ScrollAxis _axis;
        private double _startOffset;
        private double _targetOffset;
        private long _startedAt;
        private bool _running;

        public ScrollAnimation(ScrollViewer viewer, ScrollAxis axis)
        {
            _viewer = viewer;
            _axis = axis;
        }

        public void AddDelta(double delta)
        {
            var currentOffset = GetOffset();
            var accumulatedFrom = _running ? _targetOffset : currentOffset;
            _startOffset = currentOffset;
            _targetOffset = Clamp(accumulatedFrom + delta, GetScrollableLength());
            _startedAt = Stopwatch.GetTimestamp();

            if (Math.Abs(_targetOffset - _startOffset) <= BoundaryTolerance)
            {
                ScrollTo(_targetOffset);
                Stop();
                return;
            }

            if (_running)
            {
                return;
            }

            _running = true;
            CompositionTarget.Rendering += OnRendering;
        }

        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            if (!_viewer.IsLoaded)
            {
                Stop();
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
            var progress = Math.Clamp(elapsed / AnimationSeconds, 0d, 1d);
            var easedProgress = 1d - Math.Pow(1d - progress, 3d);
            var liveTarget = Clamp(_targetOffset, GetScrollableLength());
            ScrollTo(_startOffset + ((liveTarget - _startOffset) * easedProgress));

            if (progress >= 1d)
            {
                ScrollTo(liveTarget);
                Stop();
            }
        }

        private double GetOffset() =>
            _axis == ScrollAxis.Horizontal
                ? _viewer.HorizontalOffset
                : _viewer.VerticalOffset;

        private double GetScrollableLength() =>
            _axis == ScrollAxis.Horizontal
                ? _viewer.ScrollableWidth
                : _viewer.ScrollableHeight;

        private void ScrollTo(double offset)
        {
            if (_axis == ScrollAxis.Horizontal)
            {
                _viewer.ScrollToHorizontalOffset(offset);
            }
            else
            {
                _viewer.ScrollToVerticalOffset(offset);
            }
        }
    }

    private sealed class HorizontalWheelHook
    {
        private readonly HwndSource _source;
        private bool _attached;

        public HorizontalWheelHook(HwndSource source)
        {
            _source = source;
        }

        public bool IsAttached => _attached;

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _source.AddHook(WndProc);
            _source.Disposed += Source_Disposed;
            _attached = true;
        }

        private nint WndProc(
            nint hwnd,
            int message,
            nint wParam,
            nint lParam,
            ref bool handled)
        {
            _ = hwnd;
            if (message != WmMouseHorizontalWheel ||
                _source.RootVisual is not UIElement root)
            {
                return 0;
            }

            var wheelDelta = GetWheelDelta(wParam);
            if (wheelDelta == 0)
            {
                return 0;
            }

            var localPoint = root.PointFromScreen(GetScreenPoint(lParam));
            var hit = root.InputHitTest(localPoint) as DependencyObject;
            var viewer = hit is null
                ? null
                : FindHorizontalScrollableViewer(hit, wheelDelta);
            if (viewer is null)
            {
                return 0;
            }

            ApplyHorizontalWheel(viewer, wheelDelta, animate: true);
            handled = true;
            return 0;
        }

        private void Source_Disposed(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            if (!_attached)
            {
                return;
            }

            _source.RemoveHook(WndProc);
            _source.Disposed -= Source_Disposed;
            HorizontalWheelHooks.Remove(_source);
            _attached = false;
        }
    }
}
