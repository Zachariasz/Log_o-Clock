using System.Threading;
using System.Windows.Threading;

namespace ProjectTimeTracker.Windows.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\ProjectTimeTracker.Singleton";
    private const string ActivationEventName = @"Local\ProjectTimeTracker.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, bool isFirst, Dispatcher dispatcher, Action activate)
    {
        _mutex = mutex;
        IsFirstInstance = isFirst;
        if (isFirst)
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _listener = Task.Run(() => Listen(dispatcher, activate, _shutdown.Token));
        }
    }

    public bool IsFirstInstance { get; }

    public static SingleInstanceCoordinator Create(Dispatcher dispatcher, Action activate)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        return new SingleInstanceCoordinator(mutex, isFirst, dispatcher, activate);
    }

    public static void SignalExisting()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _activationEvent?.Set();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _activationEvent?.Dispose();
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }

    private void Listen(Dispatcher dispatcher, Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _activationEvent!.WaitOne();
            if (!cancellationToken.IsCancellationRequested)
            {
                dispatcher.BeginInvoke(activate);
            }
        }
    }
}
