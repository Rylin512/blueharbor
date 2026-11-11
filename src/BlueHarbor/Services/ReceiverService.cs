using System.Globalization;
using System.Windows.Threading;
using BlueHarbor.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace BlueHarbor.Services;

public enum ReceiverLinkState
{
    Off,
    Starting,
    Waiting,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

public sealed record ReceiverStatus(
    ReceiverLinkState State,
    string Title,
    string Detail,
    string? DeviceName,
    DateTime? ConnectedAt,
    int ReconnectCount,
    string? Hint = null);

/// <summary>
/// Owns the Windows A2DP sink connection. The important difference from a
/// UWP receiver is that this object lives in a normal desktop process: it is
/// never suspended, so the sink endpoint stays registered while the phone is
/// silent. A watchdog re-opens the endpoint after transient Bluetooth drops.
/// </summary>
public sealed class ReceiverService : IDisposable
{
    private readonly Logger _log;
    private readonly Dispatcher _dispatcher;
    private readonly object _deviceLock = new();
    private readonly List<ReceiverDevice> _devices = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly System.Threading.Timer _watchdog;

    private DeviceWatcher? _watcher;
    private AudioPlaybackConnection? _connection;
    private string? _connectionDeviceId;
    private bool _connectionStarted;
    private bool _armed;
    private bool _autoReconnect = true;
    private bool _disposed;
    private bool _everConnected;
    private bool _closedAfterConnection;
    private bool _waitingForLink;
    private bool _firstConnection = true;
    private bool _reengageScheduled;
    private bool _shutdownCompleted;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private DateTime? _connectedAt;
    private int _reconnects;
    private int _failureCount;
    private string? _selectedDeviceId;
    private string? _selectedDeviceName;
    private string _lastStatusKey = string.Empty;
    private ReceiverStatus _status = new(
        ReceiverLinkState.Off,
        "接收已暂停",
        "开启后，这台电脑会作为蓝牙音箱等待手机连接。",
        null,
        null,
        0);

    public ReceiverService(Logger log, Dispatcher dispatcher)
    {
        _log = log;
        _dispatcher = dispatcher;
        _watchdog = new System.Threading.Timer(_ => _ = WatchdogTickAsync(), null, 1000, 1500);
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<ReceiverStatus>? StatusChanged;
    public event EventHandler<string>? NotificationRequested;

    public bool IsArmed
    {
        get
        {
            lock (_deviceLock)
            {
                return _armed;
            }
        }
    }

    /// <summary>
    /// Set at startup when the previous process did not shut down cleanly.
    /// </summary>
    public bool ResetFirstConnectionOnStart { get; set; }

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            _autoReconnect = value;
            _log.Info(value ? "自动重连已开启" : "自动重连已关闭");
        }
    }

    public string? SelectedDeviceId
    {
        get
        {
            lock (_deviceLock)
            {
                return _selectedDeviceId;
            }
        }
    }

    public string? SelectedDeviceName
    {
        get
        {
            lock (_deviceLock)
            {
                return _selectedDeviceName;
            }
        }
    }

    public ReceiverStatus Status => _status;

    public IReadOnlyList<ReceiverDevice> Devices
    {
        get
        {
            lock (_deviceLock)
            {
                return _devices.ToArray();
            }
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            _watcher.Added += OnDeviceAdded;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();
            _log.Info("蓝牙 A2DP 接收端点监控已启动");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "无法启动蓝牙设备监控");
            Publish(ReceiverLinkState.Error, "蓝牙不可用", "请检查蓝牙适配器和系统蓝牙服务。", null, null,
                _reconnects, "错误详情已写入日志。");
        }

        await RefreshDevicesAsync();
    }

    public async Task ArmAsync(string? deviceId = null, string? deviceName = null)
    {
        lock (_deviceLock)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                _selectedDeviceId = deviceId;
                _selectedDeviceName = string.IsNullOrWhiteSpace(deviceName) ? _selectedDeviceName : deviceName;
            }

            if (string.IsNullOrWhiteSpace(_selectedDeviceId))
            {
                var first = _devices.FirstOrDefault();
                _selectedDeviceId = first?.Id;
                _selectedDeviceName = first?.Name;
            }

            _armed = !string.IsNullOrWhiteSpace(_selectedDeviceId);
            _failureCount = 0;
            _nextAttemptUtc = DateTime.MinValue;
            _closedAfterConnection = false;
            _waitingForLink = false;
        }

        if (!IsArmed)
        {
            Publish(ReceiverLinkState.Error, "没有可用的手机",
                "请先在 Windows 设置中配对手机，再回到这里开启接收。", null, null, _reconnects,
                "打开“蓝牙和其他设备”即可配对。");
            return;
        }

        _log.Info($"开启接收：{SelectedDeviceName ?? SelectedDeviceId}");
        Publish(ReceiverLinkState.Starting, "正在开启接收", "正在注册蓝牙音频接收端点…",
            SelectedDeviceName, null, _reconnects);
        await EnsureConnectionAsync();
    }

    public async Task DisarmAsync()
    {
        lock (_deviceLock)
        {
            _armed = false;
            _closedAfterConnection = false;
        }

        await DisposeConnectionAsync();
        _log.Info("接收已暂停");
        Publish(ReceiverLinkState.Off, "接收已暂停",
            "开启后，这台电脑会作为蓝牙音箱等待手机连接。", SelectedDeviceName, null, _reconnects);
    }

    public async Task SelectDeviceAsync(string deviceId, string deviceName)
    {
        bool wasArmed;
        lock (_deviceLock)
        {
            wasArmed = _armed;
            _selectedDeviceId = deviceId;
            _selectedDeviceName = deviceName;
            _closedAfterConnection = false;
        }

        await DisposeConnectionAsync();
        _log.Info($"选择音频源：{deviceName}");

        if (wasArmed)
        {
            await ArmAsync(deviceId, deviceName);
        }
        else
        {
            Publish(ReceiverLinkState.Off, "已选择音频源",
                $"开启接收后，将等待 {deviceName} 连接。", deviceName, null, _reconnects, null);
        }
    }

    public async Task RefreshDevicesAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            var fresh = found
                .Select(d => new ReceiverDevice(d.Id, string.IsNullOrWhiteSpace(d.Name) ? "未知设备" : d.Name, d.IsEnabled))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var changed = false;
            lock (_deviceLock)
            {
                changed = !_devices.SequenceEqual(fresh);
                _devices.Clear();
                _devices.AddRange(fresh);

                if (_selectedDeviceId is not null)
                {
                    var match = fresh.FirstOrDefault(d => d.Id == _selectedDeviceId);
                    if (match is not null)
                    {
                        _selectedDeviceName = match.Name;
                    }
                }
            }

            if (changed)
            {
                _log.Info($"发现 {fresh.Count} 个可用音频源");
                RaiseDevicesChanged();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "枚举蓝牙音频源失败");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task WatchdogTickAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_dispatcher.CheckAccess())
            {
                await _dispatcher.InvokeAsync(WatchdogTickAsync).Task.Unwrap();
                return;
            }

            if (_watcher is not null &&
                _watcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            {
                _log.Warn($"设备监控已停止（{_watcher.Status}），正在重启");
                try
                {
                    _watcher.Start();
                    await RefreshDevicesAsync();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "重启设备监控失败");
                }
            }

            if (!IsArmed || DateTime.UtcNow < _nextAttemptUtc)
            {
                return;
            }

            if (_connection is not null && _connection.State == AudioPlaybackConnectionState.Opened)
            {
                return;
            }

            await EnsureConnectionAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "接收看门狗异常");
        }
    }

    private async Task EnsureConnectionAsync()
    {
        if (!IsArmed || _disposed)
        {
            return;
        }

        if (!await _operationGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!IsArmed || _disposed)
            {
                return;
            }

            var targetId = SelectedDeviceId;
            var targetName = SelectedDeviceName;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }

            if (_connection is not null && _connectionDeviceId != targetId)
            {
                DisposeConnectionCore();
            }

            if (_connection is null)
            {
                var created = AudioPlaybackConnection.TryCreateFromId(targetId);
                if (created is null)
                {
                    RegisterFailure("系统没有为该设备提供音频接收端点", targetName, retrySeconds: 8);
                    return;
                }

                _connection = created;
                _connectionDeviceId = targetId;
                _connectionStarted = false;
                _connection.StateChanged += OnConnectionStateChanged;
                _log.Info($"已创建接收端点：{targetName ?? targetId}");
            }

            if (!_connectionStarted)
            {
                await _connection.StartAsync();
                _connectionStarted = true;
                _log.Info("接收端点已启用");
            }

            if (!IsArmed || _disposed)
            {
                DisposeConnectionCore();
                return;
            }

            if (_connection.State == AudioPlaybackConnectionState.Opened)
            {
                MarkConnected(targetName);
                return;
            }

            // After the first UnknownFailure, use the classic Bluetooth link
            // state as a cheap gate. OpenAsync blocks for several seconds when
            // the phone is out of range; polling the ACL link avoids a noisy
            // connect/timeout loop while still reconnecting immediately when
            // the phone comes back.
            if (_waitingForLink)
            {
                var linkConnected = await IsBluetoothLinkConnectedAsync(targetId);
                if (linkConnected == false)
                {
                    Publish(ReceiverLinkState.Waiting, "等待手机连接",
                        "请在手机上选择这台电脑 / BlueHarbor 作为蓝牙音频输出。",
                        targetName, null, _reconnects,
                        "手机播放声音时会自动连接；无需反复操作。");
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                    return;
                }

                _waitingForLink = false;
            }

            Publish(ReceiverLinkState.Connecting, "正在连接",
                $"正在与 {targetName ?? "手机"} 建立音频通道…", targetName, null, _reconnects);

            var result = await _connection.OpenAsync();
            _log.Info($"打开接收端点：{result.Status}");

            if (!IsArmed || _disposed)
            {
                DisposeConnectionCore();
                return;
            }

            switch (result.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.Success:
                    MarkConnected(targetName);
                    break;

                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    RegisterFailure("系统拒绝打开音频接收端点（可能被其他接收器占用）", targetName, retrySeconds: 12);
                    break;

                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    RegisterFailure("连接超时，正在重试", targetName, retrySeconds: 5);
                    break;

                case AudioPlaybackConnectionOpenResultStatus.UnknownFailure:
                    var linkConnected = await IsBluetoothLinkConnectedAsync(targetId);
                    if (linkConnected == false)
                    {
                        _failureCount = 0;
                        _waitingForLink = true;
                        Publish(ReceiverLinkState.Waiting, "等待手机连接",
                            "请在手机上选择这台电脑 / BlueHarbor 作为蓝牙音频输出。",
                            targetName, null, _reconnects,
                            "手机播放声音时会自动连接；无需反复操作。");
                        _nextAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                    }
                    else
                    {
                        RegisterFailure("连接失败：系统返回未知错误", targetName, retrySeconds: 8);
                    }
                    break;

                default:
                    RegisterFailure($"连接失败：{result.Status}", targetName, retrySeconds: 8);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "建立蓝牙音频接收连接失败");
            await DisposeConnectionAsync();
            RegisterFailure("连接过程中发生错误，正在自动重试", SelectedDeviceName, retrySeconds: 6);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void MarkConnected(string? deviceName)
    {
        if (_status.State == ReceiverLinkState.Connected)
        {
            return;
        }

        var shouldReengage = _firstConnection && ResetFirstConnectionOnStart && !_reengageScheduled;
        _firstConnection = false;
        if (shouldReengage)
        {
            _log.Info("上次未正常退出，首次连接将执行一次端点自愈");
        }

        if (_everConnected && _closedAfterConnection)
        {
            _reconnects++;
            _log.Info($"已自动重连（第 {_reconnects} 次）");
        }

        _everConnected = true;
        _closedAfterConnection = false;
        _waitingForLink = false;
        _failureCount = 0;
        _connectedAt = DateTime.Now;
        _log.Info($"音频通道已连接：{deviceName ?? SelectedDeviceName}");
        Publish(ReceiverLinkState.Connected, "正在接收音频",
            "手机的声音会从这台电脑播放。", deviceName, _connectedAt, _reconnects);

        PostToUi(() => NotificationRequested?.Invoke(this, $"已连接 {deviceName ?? "手机"}"));

        if (shouldReengage)
        {
            _reengageScheduled = true;
            _ = ReengageFirstConnectionAsync();
        }
    }

    /// <summary>
    /// Starting the app while the phone is still connected can inherit a stale
    /// A2DP sink route: OpenAsync reports success, but the first stream has no
    /// audio. Re-open the endpoint once, shortly after the first connection, to
    /// force Windows and the phone to re-establish the audio route.
    /// </summary>
    private async Task ReengageFirstConnectionAsync()
    {
        try
        {
            await Task.Delay(700);
            if (_disposed || !IsArmed || _connection is null ||
                _connection.State != AudioPlaybackConnectionState.Opened)
            {
                return;
            }

            _log.Info("首次连接自愈：重新挂接 A2DP 端点");
            await DisposeConnectionAsync();
            await Task.Delay(250);

            if (!_disposed && IsArmed)
            {
                _nextAttemptUtc = DateTime.MinValue;
                await EnsureConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"首次连接自愈失败：{ex.Message}");
        }
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        PostToUi(() => HandleConnectionStateChanged(sender));
    }

    private void HandleConnectionStateChanged(AudioPlaybackConnection sender)
    {
        if (_disposed || !ReferenceEquals(sender, _connection))
        {
            return;
        }

        var state = sender.State;
        _log.Info($"接收端点状态变更：{state}");

        if (state == AudioPlaybackConnectionState.Opened)
        {
            MarkConnected(SelectedDeviceName);
            return;
        }

        _connectedAt = null;
        _waitingForLink = true;
        if (_everConnected)
        {
            _closedAfterConnection = true;
        }

        if (!IsArmed)
        {
            Publish(ReceiverLinkState.Off, "接收已暂停",
                "开启后，这台电脑会作为蓝牙音箱等待手机连接。", SelectedDeviceName, null, _reconnects);
            return;
        }

        Publish(ReceiverLinkState.Reconnecting, "连接已断开",
            "接收端仍在后台保持启用，正在等待手机重新连接…", SelectedDeviceName, null, _reconnects,
            "如果手机蓝牙已关闭，重新打开后会自动恢复。");
        _log.Warn("音频通道已断开，看门狗将自动重试");
        _nextAttemptUtc = DateTime.UtcNow.AddMilliseconds(350);

        PostToUi(() => NotificationRequested?.Invoke(this, "音频连接已断开，正在自动重连"));
    }

    private static async Task<bool?> IsBluetoothLinkConnectedAsync(string deviceId)
    {
        try
        {
            // Device ids end with ..._<12 hex address>_C00000000.
            var marker = deviceId.LastIndexOf("_C", StringComparison.OrdinalIgnoreCase);
            if (marker < 12)
            {
                return null;
            }

            var hex = deviceId.Substring(marker - 12, 12);
            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                return null;
            }

            var device = await BluetoothDevice.FromBluetoothAddressAsync(address);
            return device is null ? null : device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch
        {
            return null;
        }
    }

    private void RegisterFailure(string message, string? deviceName, int retrySeconds)
    {
        _failureCount++;
        var delay = Math.Min(30, Math.Max(retrySeconds, 2 + _failureCount * 2));
        _nextAttemptUtc = DateTime.UtcNow.AddSeconds(delay);
        _log.Warn($"{message}（{delay} 秒后重试）");
        Publish(ReceiverLinkState.Error, "连接需要处理", message, deviceName, null, _reconnects,
            $"{delay} 秒后自动重试。");
    }

    private async Task DisposeConnectionAsync()
    {
        if (!await _operationGate.WaitAsync(0))
        {
            // An in-flight operation owns the object; it checks IsArmed before
            // returning and will dispose the connection itself.
            return;
        }

        try
        {
            DisposeConnectionCore();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void DisposeConnectionCore()
    {
        var connection = _connection;
        _connection = null;
        _connectionDeviceId = null;
        _connectionStarted = false;
        if (connection is null)
        {
            return;
        }

        connection.StateChanged -= OnConnectionStateChanged;
        try
        {
            connection.Dispose();
            _log.Info("接收端点已释放");
        }
        catch (Exception ex)
        {
            _log.Warn($"释放接收端点时出错：{ex.Message}");
        }
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
    {
        PostToUi(() =>
        {
            _log.Info($"发现蓝牙音频源：{args.Name}");
            _ = RefreshDevicesAsync();
            _nextAttemptUtc = DateTime.MinValue;
        });
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        PostToUi(() =>
        {
            _log.Warn($"蓝牙音频源已移除：{args.Id}");
            var removedSelected = false;
            lock (_deviceLock)
            {
                _devices.RemoveAll(d => d.Id == args.Id);
                removedSelected = args.Id == _selectedDeviceId;
            }
            RaiseDevicesChanged();

            if (removedSelected && IsArmed)
            {
                _waitingForLink = true;
                _ = DisposeConnectionAsync();
                Publish(ReceiverLinkState.Waiting, "设备已断开",
                    "手机已从系统蓝牙中移除或关闭，重新配对后会自动恢复。", SelectedDeviceName, null, _reconnects);
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            }
        });
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        PostToUi(() => _ = RefreshDevicesAsync());
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        PostToUi(() =>
        {
            _log.Info("蓝牙音频源枚举完成");
            _ = RefreshDevicesAsync();
        });
    }

    private void OnWatcherStopped(DeviceWatcher sender, object args)
    {
        PostToUi(() => _log.Warn($"蓝牙设备监控停止：{sender.Status}"));
    }

    private void PostToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private void RaiseDevicesChanged()
    {
        _dispatcher.BeginInvoke(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
    }

    private void Publish(ReceiverLinkState state, string title, string detail, string? deviceName,
        DateTime? connectedAt, int reconnects, string? hint = null)
    {
        var next = new ReceiverStatus(state, title, detail, deviceName, connectedAt, reconnects, hint);
        var key = $"{state}|{title}|{detail}|{deviceName}|{hint}|{reconnects}|{connectedAt?.Ticks}";
        if (key == _lastStatusKey)
        {
            return;
        }

        _lastStatusKey = key;
        _status = next;
        _dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, next));
    }

    /// <summary>
    /// Graceful shutdown used by the tray exit path. Give the A2DP stack a
    /// moment to tear the endpoint down before the process exits; otherwise the
    /// phone can keep a stale sink route, which makes the next launch's first
    /// stream silent.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_shutdownCompleted)
        {
            return;
        }
        _shutdownCompleted = true;
        _disposed = true;
        lock (_deviceLock)
        {
            _armed = false;
        }

        try
        {
            _watchdog.Dispose();
        }
        catch
        {
            // Ignore.
        }

        StopWatcher();
        await DisposeConnectionAsync();
        DisposeConnectionCore();
        await Task.Delay(700);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_deviceLock)
        {
            _armed = false;
        }

        try
        {
            _watchdog.Dispose();
        }
        catch
        {
            // Ignore.
        }

        StopWatcher();
        DisposeConnectionCore();

        // Last-resort synchronous path (session ending). Short block is
        // preferable to leaving a stale A2DP route on the phone.
        Thread.Sleep(400);
    }

    private void StopWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.Added -= OnDeviceAdded;
            _watcher.Removed -= OnDeviceRemoved;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnWatcherStopped;
            _watcher.Stop();
        }
        catch
        {
            // Ignore.
        }
    }
}
