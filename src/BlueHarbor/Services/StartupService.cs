using Microsoft.Win32;

namespace BlueHarbor.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BlueHarbor";
    private readonly Logger _log;

    public StartupService(Logger log)
    {
        _log = log;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    return;
                }
                key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
                _log.Info("已启用开机启动");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Info("已关闭开机启动");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "更新开机启动设置失败");
        }
    }
}
