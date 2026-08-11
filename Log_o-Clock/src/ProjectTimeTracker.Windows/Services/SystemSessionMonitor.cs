using Microsoft.Win32;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class SystemSessionMonitor : ISystemSessionMonitor
{
    private bool _started;

    public event EventHandler<SystemSessionEvent>? SessionChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        _ = sender;
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            SessionChanged?.Invoke(this, SystemSessionEvent.Locked);
        }
        else if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            SessionChanged?.Invoke(this, SystemSessionEvent.Unlocked);
        }
        else if (args.Reason == SessionSwitchReason.SessionLogoff)
        {
            SessionChanged?.Invoke(this, SystemSessionEvent.SigningOut);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        _ = sender;
        if (args.Mode == PowerModes.Suspend)
        {
            SessionChanged?.Invoke(this, SystemSessionEvent.Suspending);
        }
        else if (args.Mode == PowerModes.Resume)
        {
            SessionChanged?.Invoke(this, SystemSessionEvent.Resumed);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs args)
    {
        _ = sender;
        SessionChanged?.Invoke(
            this,
            args.Reason == SessionEndReasons.Logoff
                ? SystemSessionEvent.SigningOut
                : SystemSessionEvent.Ending);
    }
}
