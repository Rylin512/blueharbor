using System.Runtime.InteropServices;

namespace BlueHarbor.Services;

public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);
public sealed record AudioAudibilityResult(bool WasMuted, float Volume, bool Changed);

/// <summary>
/// Enumerates active render endpoints and switches the Windows default
/// playback device. AudioPlaybackConnection always renders to the current
/// default endpoint, so a stale USB/headphone default is a common cause of
/// "connected but no sound".
/// </summary>
public sealed class AudioOutputService
{
    private const int EDataFlowRender = 0;
    private const int EDataFlowAll = 2;
    private const int DeviceStateActive = 1;
    private const int StgmRead = 0;

    private static readonly PropertyKey FriendlyNameKey = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14
    };

    private readonly Logger _log;

    public AudioOutputService(Logger log)
    {
        _log = log;
    }

    public IReadOnlyList<AudioOutputDevice> GetActiveRenderDevices()
    {
        var result = new List<AudioOutputDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            var hr = enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out var collection);
            if (hr != 0 || collection is null)
            {
                _log.Warn($"枚举音频输出设备失败：0x{hr:X8}");
                return result;
            }

            var defaultId = GetDefaultDeviceId(enumerator);
            collection.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0)
                {
                    continue;
                }

                device.GetId(out var id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var name = GetFriendlyName(device) ?? id;
                result.Add(new AudioOutputDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取音频输出设备失败：{ex.Message}");
        }

        return result
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var policy = (IPolicyConfig)(new PolicyConfigClient());
            var ok = true;
            for (var role = 0; role <= 2; role++)
            {
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    ok = false;
                    _log.Warn($"设置默认音频输出失败（role={role}）：0x{hr:X8}");
                }
            }

            if (ok)
            {
                _log.Info($"已将默认音频输出切换为：{deviceId}");
            }

            return ok;
        }
        catch (Exception ex)
        {
            _log.Warn($"切换默认音频输出失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Windows can keep the render endpoint muted after a device switch.
    /// A2DP then opens successfully while the user hears nothing. Repair the
    /// mute state and lift a near-zero volume to an audible floor.
    /// </summary>
    public AudioAudibilityResult EnsureAudible()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, 1, out var device) != 0)
            {
                return new(false, 0, false);
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out var instance) != 0 ||
                instance is not IAudioEndpointVolume volume)
            {
                return new(false, 0, false);
            }

            var context = Guid.Empty;
            volume.GetMute(out var muted);
            volume.GetMasterVolumeLevelScalar(out var scalar);
            var changed = false;

            if (muted)
            {
                volume.SetMute(false, ref context);
                changed = true;
                _log.Warn("默认音频输出处于静音状态，已自动取消静音");
            }

            if (scalar < 0.15f)
            {
                volume.SetMasterVolumeLevelScalar(0.35f, ref context);
                changed = true;
                _log.Warn($"默认音频输出音量过低（{scalar:P0}），已提升到 35%");
            }

            return new(muted, scalar, changed);
        }
        catch (Exception ex)
        {
            _log.Warn($"检查默认音频输出音量失败：{ex.Message}");
            return new(false, 0, false);
        }
    }

    private static string? GetDefaultDeviceId(IMMDeviceEnumerator enumerator)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, 1, out var device) != 0)
            {
                return null;
            }

            device.GetId(out var id);
            return id;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0)
        {
            return null;
        }

        var key = FriendlyNameKey;
        var value = default(PropVariant);
        if (store.GetValue(ref key, out value) != 0)
        {
            return null;
        }

        try
        {
            return value.VarType == 31 && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.Pointer)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool visible);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
