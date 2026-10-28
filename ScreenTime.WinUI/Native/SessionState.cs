using Microsoft.Win32;

namespace ScreenTime.Native;

/// <summary>锁屏 / 睡眠状态跟踪。</summary>
public sealed class SessionState : IDisposable
{
    public bool IsLocked { get; private set; }
    public bool IsSuspended { get; private set; }

    public event Action? Locked;
    public event Action? Unlocked;
    public event Action? Resumed;
    public event Action? Suspended;

    public void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                IsLocked = true;
                Locked?.Invoke();
                break;
            case SessionSwitchReason.SessionUnlock:
                IsLocked = false;
                Unlocked?.Invoke();
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                IsSuspended = true;
                Suspended?.Invoke();
                break;
            case PowerModes.Resume:
                IsSuspended = false;
                Resumed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
