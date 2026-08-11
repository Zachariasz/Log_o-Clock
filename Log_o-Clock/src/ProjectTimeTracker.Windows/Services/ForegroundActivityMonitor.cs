using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class ForegroundActivityMonitor : IForegroundActivityMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private readonly WinEventDelegate _callback;
    private nint _hook;
    private bool _disposed;

    public ForegroundActivityMonitor()
    {
        _callback = OnWinEvent;
    }

    public event EventHandler<WindowActivity>? ActivityChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);

        if (_hook == nint.Zero)
        {
            throw new InvalidOperationException("Windows did not create the foreground-window event hook.");
        }
    }

    public WindowActivity? GetCurrentActivity() => ReadActivity(GetForegroundWindow());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != nint.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        _ = hook;
        _ = eventType;
        _ = idObject;
        _ = idChild;
        _ = eventThread;
        _ = eventTime;

        var activity = ReadActivity(hwnd);
        if (activity is not null)
        {
            ActivityChanged?.Invoke(this, activity);
        }
    }

    private static WindowActivity? ReadActivity(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return null;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, title, title.Capacity);
        if (title.Length == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        var processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process can exit between the window event and this lookup.
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return new WindowActivity(hwnd, title.ToString(), processName, DateTimeOffset.UtcNow);
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
