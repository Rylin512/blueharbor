using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace BlueHarbor.Services;

public sealed record AacEnableResult(bool Success, string Message);

/// <summary>
/// Windows has no public per-connection A2DP codec selector. The documented
/// machine-wide lever is the BthA2dp BluetoothAacEnable value: when AAC is
/// allowed, Windows prefers AAC over SBC and automatically falls back to SBC
/// when the phone/adapter cannot negotiate AAC.
/// </summary>
public sealed class AacPreferenceService
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Services\BthA2dp\Parameters";
    private const string ValueName = "BluetoothAacEnable";
    private readonly Logger _log;

    public AacPreferenceService(Logger log)
    {
        _log = log;
    }

    public bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
                return key?.GetValue(ValueName) is int value && value != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<AacEnableResult> DisableAsync()
    {
        if (!IsWindows11)
        {
            return new(false, "AAC A2DP 需要 Windows 11 21H2 或更高版本。");
        }

        if (!IsEnabled)
        {
            return new(true, "当前已经是 SBC 兼容模式。");
        }

        try
        {
            var reg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "reg.exe");
            var args =
                $"add \"HKLM\\{KeyPath}\" /v {ValueName} /t REG_DWORD /d 0 /f";

            var startInfo = new ProcessStartInfo
            {
                FileName = reg,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(false, "无法启动管理员权限的注册表设置进程。");
            }

            await process.WaitForExitAsync();
            await Task.Delay(350);

            if (!IsEnabled)
            {
                _log.Info("已回退 SBC 兼容模式（BluetoothAacEnable=0）");
                return new(true,
                    "已回退 SBC。请暂停/开启接收，或重新连接手机，让蓝牙栈重新协商为 SBC。");
            }

            return new(false, "设置命令已执行，但 Windows 仍在允许 AAC。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "已取消管理员授权，仍保持 AAC 模式。");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "回退 SBC 失败");
            return new(false, $"回退 SBC 失败：{ex.Message}");
        }
    }

    public async Task<AacEnableResult> EnableAsync()
    {
        if (!IsWindows11)
        {
            return new(false, "AAC A2DP 需要 Windows 11 21H2 或更高版本。");
        }

        if (IsEnabled)
        {
            return new(true, "AAC 优先已经是启用状态。");
        }

        try
        {
            var reg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "reg.exe");
            var args =
                $"add \"HKLM\\{KeyPath}\" /v {ValueName} /t REG_DWORD /d 1 /f";

            var startInfo = new ProcessStartInfo
            {
                FileName = reg,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(false, "无法启动管理员权限的注册表设置进程。");
            }

            await process.WaitForExitAsync();
            await Task.Delay(350);

            if (IsEnabled)
            {
                _log.Info("已启用 Windows AAC 优先（BluetoothAacEnable=1）");
                return new(true,
                    "AAC 优先已启用。请暂停/开启接收，或重新连接手机，让蓝牙栈重新协商编码。");
            }

            _log.Warn("AAC 设置命令已执行，但注册表值仍未生效");
            return new(false, "设置命令已执行，但 Windows 未确认 AAC 已启用。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "已取消管理员授权，AAC 优先未启用。");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "启用 AAC 优先失败");
            return new(false, $"启用 AAC 优先失败：{ex.Message}");
        }
    }
}
