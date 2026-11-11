using System.Runtime.InteropServices;

namespace BlueHarbor.Services;

/// <summary>
/// Keeps the system awake while a receiver connection is active. The execution
/// state is per-thread, so all calls are marshalled onto the WPF UI thread,
/// which lives for the entire process lifetime.
/// </summary>
public sealed class PowerService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private readonly Logger _log;
    private bool _active;

    public PowerService(Logger log)
    {
        _log = log;
    }

    public bool IsActive => _active;

    public void SetKeepAwake(bool active)
    {
        if (_active == active)
        {
            return;
        }

        try
        {
            var flags = EsContinuous | (active ? EsSystemRequired : 0);
            var result = SetThreadExecutionState(flags);
            if (result == 0 && active)
            {
                _log.Warn("SetThreadExecutionState 失败，系统可能仍会休眠");
                return;
            }

            _active = active;
            _log.Info(active ? "已阻止系统在连接期间休眠" : "已恢复系统休眠策略");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "切换系统休眠策略失败");
        }
    }

    public void Release()
    {
        try
        {
            SetThreadExecutionState(EsContinuous);
            _active = false;
        }
        catch
        {
            // Best effort.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
