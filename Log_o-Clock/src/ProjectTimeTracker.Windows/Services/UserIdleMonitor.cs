using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Threading;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class UserIdleMonitor : IUserIdleMonitor
{
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _threshold;
    private readonly IIdleProtectionMonitor _protectionMonitor;
    private IdleProtectionState _protectionState = IdleProtectionState.NotStarted;
    private long? _protectionEndedTimestamp;
    private bool _started;
    private bool _disposed;

    public UserIdleMonitor(
        TimeSpan threshold,
        IIdleProtectionMonitor protectionMonitor)
    {
        _threshold = threshold;
        _protectionMonitor = protectionMonitor;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += OnTick;
        _protectionMonitor.StateChanged += OnProtectionChanged;
    }

    public event EventHandler<DateTimeOffset>? IdleStarted;
    public event EventHandler<DateTimeOffset>? ActivityResumed;
    public bool IsIdle { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _protectionMonitor.StateChanged -= OnProtectionChanged;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_protectionState.IsProtected)
        {
            return;
        }

        var idleFor = GetEffectiveIdleDuration();
        if (!IsIdle && idleFor >= _threshold)
        {
            IsIdle = true;
            IdleStarted?.Invoke(this, DateTimeOffset.UtcNow - idleFor);
        }
        else if (IsIdle && idleFor < TimeSpan.FromSeconds(3))
        {
            IsIdle = false;
            ActivityResumed?.Invoke(this, DateTimeOffset.UtcNow);
        }
    }

    private void OnProtectionChanged(object? sender, IdleProtectionState state)
    {
        _ = sender;
        _timer.Dispatcher.BeginInvoke(() => ApplyProtectionState(state));
    }

    private void ApplyProtectionState(IdleProtectionState state)
    {
        if (_disposed)
        {
            return;
        }

        var wasProtected = _protectionState.IsProtected;
        _protectionState = state;
        if (!wasProtected && state.IsProtected)
        {
            // AppController timestamps and defers review of any real idle portion.
            // This monitor only prevents the protected interval from continuing it.
            IsIdle = false;
        }
        else if (wasProtected && !state.IsProtected)
        {
            _protectionEndedTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private TimeSpan GetEffectiveIdleDuration()
    {
        var idleFor = GetIdleDuration();
        if (_protectionEndedTimestamp is not { } protectionEnded)
        {
            return idleFor;
        }

        var elapsedSinceProtection = TimeSpan.FromSeconds(
            Math.Max(
                0,
                (Stopwatch.GetTimestamp() - protectionEnded) /
                (double)Stopwatch.Frequency));
        return idleFor <= elapsedSinceProtection ? idleFor : elapsedSinceProtection;
    }

    private static TimeSpan GetIdleDuration()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(Math.Max(0, elapsed));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
