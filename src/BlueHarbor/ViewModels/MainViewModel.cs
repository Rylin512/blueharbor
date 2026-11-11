using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BlueHarbor.Models;
using BlueHarbor.Services;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace BlueHarbor.ViewModels;

public sealed record TrayNotification(string Title, string Message, bool IsWarning = false);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Logger _log;
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly PowerService _powerService;
    private readonly AacPreferenceService _aacPreference;
    private readonly AudioOutputService _audioOutput;
    private readonly ReceiverService _receiver;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _uptimeTimer;

    private bool _isArmed;
    private bool _isConnected;
    private bool _isBusy;
    private bool _hasHint;
    private bool _autoReconnect;
    private bool _keepAwake;
    private bool _autoStart;
    private bool _closeToTray;
    private bool _showNotifications;
    private bool _startMinimized;
    private bool _isAacEnabled;
    private bool _canEnableAac;
    private bool _isLoadingOutput;
    private AudioOutputDevice? _selectedOutputDevice;
    private string _aacStatusText = "正在检查音频兼容模式…";
    private string _outputStatusText = "正在读取默认音频输出…";
    private string _statusTitle = "接收已暂停";
    private string _statusDetail = "开启后，这台电脑会作为蓝牙音箱等待手机连接。";
    private string _statusHint = string.Empty;
    private string _deviceName = "未选择设备";
    private string _uptimeText = "--:--:--";
    private string _connectedSinceText = "尚未连接";
    private string _reconnectText = "本次会话首次连接";
    private string _lastEventText = "等待操作";
    private string _adapterText = "正在读取蓝牙适配器…";
    private string _codecText = "SBC（稳定兼容模式）";
    private string _osText = string.Empty;
    private string _appVersionText = string.Empty;
    private DateTime? _connectedAt;
    private int _reconnectCount;
    private ReceiverLinkState _linkState = ReceiverLinkState.Off;
    private bool _disposed;

    public MainViewModel(
        Logger log,
        AppSettings settings,
        SettingsService settingsService,
        StartupService startupService,
        PowerService powerService,
        AacPreferenceService aacPreference,
        AudioOutputService audioOutput,
        ReceiverService receiver)
    {
        _log = log;
        _settings = settings;
        _settingsService = settingsService;
        _startupService = startupService;
        _powerService = powerService;
        _aacPreference = aacPreference;
        _audioOutput = audioOutput;
        _receiver = receiver;

        _autoReconnect = settings.AutoReconnect;
        _keepAwake = settings.KeepAwake;
        _autoStart = startupService.IsEnabled;
        _closeToTray = settings.CloseToTray;
        _showNotifications = settings.ShowNotifications;
        _startMinimized = settings.StartMinimized;
        _receiver.AutoReconnect = _autoReconnect;

        ToggleArmCommand = new AsyncRelayCommand(ToggleArmAsync);
        SelectDeviceCommand = new AsyncRelayCommand<DeviceItemViewModel>(SelectDeviceAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenBluetoothSettingsCommand = new RelayCommand(OpenBluetoothSettings);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ShowQualityInfoCommand = new RelayCommand(ShowQualityInfo);
        EnableAacCommand = new AsyncRelayCommand(EnableAacAsync);
        DisableAacCommand = new AsyncRelayCommand(DisableAacAsync);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        _receiver.DevicesChanged += OnDevicesChanged;
        _receiver.StatusChanged += OnStatusChanged;
        _receiver.NotificationRequested += OnReceiverNotification;

        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uptimeTimer.Tick += (_, _) => UpdateUptime();
        _uptimeTimer.Start();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _appVersionText = $"BlueHarbor {version?.Major}.{version?.Minor}.{version?.Build}";
        _osText = $"Windows {Environment.OSVersion.Version.Build} · {RuntimeInformation.OSArchitecture}";
    }

    public event EventHandler<TrayNotification>? NotificationRequested;
    public event EventHandler? ExitRequested;

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = new();

    public AppSettings Settings => _settings;

    public ICommand ToggleArmCommand { get; }
    public ICommand SelectDeviceCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenBluetoothSettingsCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public ICommand ShowQualityInfoCommand { get; }
    public ICommand EnableAacCommand { get; }
    public ICommand DisableAacCommand { get; }
    public ICommand ExitCommand { get; }

    public bool IsArmed
    {
        get => _isArmed;
        private set
        {
            if (SetProperty(ref _isArmed, value))
            {
                OnPropertyChanged(nameof(ArmButtonText));
                OnPropertyChanged(nameof(ArmButtonHint));
                OnPropertyChanged(nameof(IsDisarmed));
            }
        }
    }

    public bool IsDisarmed => !IsArmed;

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasHint
    {
        get => _hasHint;
        private set => SetProperty(ref _hasHint, value);
    }

    public ReceiverLinkState LinkState
    {
        get => _linkState;
        private set
        {
            if (SetProperty(ref _linkState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(StatusAccent));
            }
        }
    }

    public bool IsConnecting => LinkState is ReceiverLinkState.Starting or ReceiverLinkState.Connecting or ReceiverLinkState.Reconnecting or ReceiverLinkState.Waiting;
    public bool IsError => LinkState == ReceiverLinkState.Error;

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
    }

    public string ConnectedSinceText
    {
        get => _connectedSinceText;
        private set => SetProperty(ref _connectedSinceText, value);
    }

    public string ReconnectText
    {
        get => _reconnectText;
        private set => SetProperty(ref _reconnectText, value);
    }

    public string LastEventText
    {
        get => _lastEventText;
        private set => SetProperty(ref _lastEventText, value);
    }

    public string AdapterText
    {
        get => _adapterText;
        private set => SetProperty(ref _adapterText, value);
    }

    public string CodecText
    {
        get => _codecText;
        private set => SetProperty(ref _codecText, value);
    }

    public bool IsAacEnabled
    {
        get => _isAacEnabled;
        private set => SetProperty(ref _isAacEnabled, value);
    }

    public bool CanEnableAac
    {
        get => _canEnableAac;
        private set => SetProperty(ref _canEnableAac, value);
    }

    public string AacStatusText
    {
        get => _aacStatusText;
        private set => SetProperty(ref _aacStatusText, value);
    }

    public AudioOutputDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value))
            {
                return;
            }

            if (_isLoadingOutput || value is null)
            {
                return;
            }

            _settings.OutputDeviceId = value.Id;
            _settings.OutputDeviceName = value.Name;
            SaveSettings();

            var ok = _audioOutput.SetDefaultDevice(value.Id);
            if (ok)
            {
                RepairAudibility();
            }
            OutputStatusText = ok
                ? $"当前输出：{value.Name}（已设为系统默认）"
                : $"切换失败，当前仍为：{value.Name}";
            LastEventText = ok
                ? $"输出设备已切换到 {value.Name}"
                : "切换输出设备失败，请查看日志";

            // Re-open the A2DP endpoint so a newly selected endpoint is used
            // immediately instead of at the next phone reconnect.
            if (IsArmed)
            {
                _ = ReconnectForOutputChangeAsync();
            }
        }
    }

    public string OutputStatusText
    {
        get => _outputStatusText;
        private set => SetProperty(ref _outputStatusText, value);
    }

    public string OsText
    {
        get => _osText;
        private set => SetProperty(ref _osText, value);
    }

    public string AppVersionText
    {
        get => _appVersionText;
        private set => SetProperty(ref _appVersionText, value);
    }

    public bool HasDevices => Devices.Count > 0;
    public bool IsEmpty => Devices.Count == 0;
    public string DeviceCountText => Devices.Count == 0 ? "尚未配对手机" : $"{Devices.Count} 个已配对音频源";
    public string ArmButtonText => IsArmed ? "暂停接收" : "开启接收";
    public string ArmButtonHint => IsArmed ? "接收端会保持后台运行" : "电脑将作为蓝牙音箱待命";
    public string StatusAccent => LinkState switch
    {
        ReceiverLinkState.Connected => "Mint",
        ReceiverLinkState.Error => "Red",
        ReceiverLinkState.Off => "Muted",
        _ => "Amber"
    };

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            if (SetProperty(ref _autoReconnect, value))
            {
                _receiver.AutoReconnect = value;
                _settings.AutoReconnect = value;
                SaveSettings();
            }
        }
    }

    public bool KeepAwake
    {
        get => _keepAwake;
        set
        {
            if (SetProperty(ref _keepAwake, value))
            {
                _settings.KeepAwake = value;
                if (IsConnected)
                {
                    _powerService.SetKeepAwake(value);
                }
                SaveSettings();
            }
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (SetProperty(ref _autoStart, value))
            {
                _startupService.SetEnabled(value);
                _settings.AutoStart = value;
                SaveSettings();
            }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
            {
                _settings.CloseToTray = value;
                SaveSettings();
            }
        }
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set
        {
            if (SetProperty(ref _showNotifications, value))
            {
                _settings.ShowNotifications = value;
                SaveSettings();
            }
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (SetProperty(ref _startMinimized, value))
            {
                _settings.StartMinimized = value;
                SaveSettings();
            }
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (_settings.AutoStart != _autoStart)
            {
                _settings.AutoStart = _autoStart;
            }

            await _receiver.InitializeAsync();
            RefreshDevicesFromReceiver();

            if (_settings.Armed)
            {
                var id = _settings.SelectedDeviceId;
                var name = _settings.SelectedDeviceName;
                if (string.IsNullOrWhiteSpace(id) && Devices.Count > 0)
                {
                    id = Devices[0].Id;
                    name = Devices[0].Name;
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    await _receiver.ArmAsync(id, name);
                }
            }
            else
            {
                IsArmed = false;
                ApplyStatus(_receiver.Status, initial: true);
            }

            await LoadAdapterInfoAsync();
            LoadAacStatus();
            LoadOutputDevices();
            _log.Info("BlueHarbor 初始化完成");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "初始化失败");
            StatusTitle = "初始化失败";
            StatusDetail = "请查看日志了解详情，或重新启动 BlueHarbor。";
            LinkState = ReceiverLinkState.Error;
            HasHint = true;
            StatusHint = ex.Message;
        }
    }

    private void LoadAacStatus()
    {
        IsAacEnabled = _aacPreference.IsEnabled;
        CanEnableAac = _aacPreference.IsWindows11 && !IsAacEnabled;

        if (!_aacPreference.IsWindows11)
        {
            AacStatusText = "SBC 兼容模式（当前 Windows 不支持 AAC A2DP）";
            CodecText = "SBC（稳定兼容模式）";
        }
        else if (IsAacEnabled)
        {
            AacStatusText = "AAC 实验模式已开启；若连接后无声，请点“回退 SBC”";
            CodecText = "AAC（实验）";
        }
        else
        {
            AacStatusText = "SBC 兼容模式（推荐，最稳定）";
            CodecText = "SBC（稳定兼容模式）";
        }
    }

    private async Task EnableAacAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var answer = MessageBox.Show(
            "AAC 是实验功能。部分手机/适配器会出现“显示已连接但完全没有声音”。\n\n" +
            "BlueHarbor 默认使用 SBC，正是为了避免这个问题。只有确认你的设备兼容 AAC 时才建议开启。\n\n" +
            "开启需要管理员权限，并会修改机器级蓝牙设置。继续？",
            "启用 AAC（实验）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _aacPreference.EnableAsync();
            LoadAacStatus();
            LastEventText = result.Success
                ? $"AAC 实验模式已启用 · {DateTime.Now:HH:mm:ss}"
                : $"AAC 设置未完成 · {DateTime.Now:HH:mm:ss}";
            MessageBox.Show(result.Message, "音频协议",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisableAacAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _aacPreference.DisableAsync();
            LoadAacStatus();
            LastEventText = result.Success
                ? $"已回退 SBC · {DateTime.Now:HH:mm:ss}"
                : $"SBC 回退未完成 · {DateTime.Now:HH:mm:ss}";
            MessageBox.Show(result.Message, "音频协议",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadOutputDevices()
    {
        try
        {
            _isLoadingOutput = true;
            var devices = _audioOutput.GetActiveRenderDevices();
            OutputDevices.Clear();
            foreach (var device in devices)
            {
                OutputDevices.Add(device);
            }

            var savedId = _settings.OutputDeviceId;
            var selected = !string.IsNullOrWhiteSpace(savedId)
                ? OutputDevices.FirstOrDefault(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase))
                : null;
            selected ??= OutputDevices.FirstOrDefault(d => d.IsDefault) ?? OutputDevices.FirstOrDefault();

            if (selected is not null && !string.IsNullOrWhiteSpace(savedId) &&
                string.Equals(selected.Id, savedId, StringComparison.OrdinalIgnoreCase) &&
                !selected.IsDefault)
            {
                // The user picked this endpoint before; Windows may have moved
                // the default to a dead USB/headphone endpoint. Put it back.
                _audioOutput.SetDefaultDevice(selected.Id);
                OutputStatusText = $"已自动恢复输出：{selected.Name}";
                if (IsArmed)
                {
                    _ = ReconnectForOutputChangeAsync();
                }
            }
            else
            {
                OutputStatusText = selected is null
                    ? "未发现可用的音频输出设备"
                    : $"当前输出：{selected.Name}";
            }

            _selectedOutputDevice = selected;
            OnPropertyChanged(nameof(SelectedOutputDevice));
            RepairAudibility();
            _log.Info($"默认音频输出：{selected?.Name ?? "无"}");
        }
        catch (Exception ex)
        {
            _log.Warn($"读取音频输出失败：{ex.Message}");
            OutputStatusText = "读取音频输出失败";
        }
        finally
        {
            _isLoadingOutput = false;
        }
    }

    private void RepairAudibility()
    {
        var result = _audioOutput.EnsureAudible();
        if (!result.Changed)
        {
            return;
        }

        OutputStatusText = result.WasMuted
            ? "检测到系统静音，已自动取消静音"
            : "检测到系统音量过低，已自动提升";
        LastEventText = $"{OutputStatusText} · {DateTime.Now:HH:mm:ss}";
        _log.Info(OutputStatusText);
    }

    private async Task ReconnectForOutputChangeAsync()
    {
        try
        {
            await _receiver.DisarmAsync();
            await Task.Delay(450);
            await _receiver.ArmAsync(_settings.SelectedDeviceId, _settings.SelectedDeviceName);
        }
        catch (Exception ex)
        {
            _log.Warn($"切换输出后重连接口失败：{ex.Message}");
        }
    }

    private async Task ToggleArmAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (_receiver.IsArmed)
            {
                await _receiver.DisarmAsync();
            }
            else
            {
                await _receiver.ArmAsync();
            }

            IsArmed = _receiver.IsArmed;
            SaveSettings();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "切换接收状态失败");
            StatusTitle = "操作失败";
            StatusDetail = ex.Message;
            LinkState = ReceiverLinkState.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SelectDeviceAsync(DeviceItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.Id == _receiver.SelectedDeviceId && _receiver.IsArmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _receiver.SelectDeviceAsync(item.Id, item.Name);
            IsArmed = _receiver.IsArmed;
            SaveSettings();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "切换音频源失败");
            StatusTitle = "切换失败";
            StatusDetail = ex.Message;
            LinkState = ReceiverLinkState.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _receiver.RefreshDevicesAsync();
            RefreshDevicesFromReceiver();
            LastEventText = $"设备列表已刷新 · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => RefreshDevicesFromReceiver();

    private void RefreshDevicesFromReceiver()
    {
        var selectedId = _receiver.SelectedDeviceId;
        var connected = LinkState == ReceiverLinkState.Connected;
        var items = _receiver.Devices
            .Select(d => new DeviceItemViewModel(d, d.Id == selectedId, connected && d.Id == selectedId))
            .ToList();

        Devices.Clear();
        foreach (var item in items)
        {
            Devices.Add(item);
        }

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(DeviceCountText));

        if (string.IsNullOrWhiteSpace(_receiver.SelectedDeviceName) && Devices.Count > 0)
        {
            DeviceName = "未选择设备";
        }
    }

    private void OnStatusChanged(object? sender, ReceiverStatus status) => ApplyStatus(status);

    private void ApplyStatus(ReceiverStatus status, bool initial = false)
    {
        var previous = LinkState;
        LinkState = status.State;
        if (status.State == ReceiverLinkState.Connected && previous != ReceiverLinkState.Connected)
        {
            RepairAudibility();
        }
        StatusTitle = status.Title;
        StatusDetail = status.Detail;
        StatusHint = status.Hint ?? string.Empty;
        HasHint = !string.IsNullOrWhiteSpace(status.Hint);
        DeviceName = status.DeviceName ?? _receiver.SelectedDeviceName ?? "未选择设备";
        ReconnectCountText(status.ReconnectCount);

        if (status.ConnectedAt is { } connectedAt)
        {
            _connectedAt = connectedAt;
        }
        else if (status.State != ReceiverLinkState.Connected)
        {
            _connectedAt = null;
        }

        IsArmed = _receiver.IsArmed;
        IsConnected = status.State == ReceiverLinkState.Connected;
        ConnectedSinceText = _connectedAt is null
            ? "尚未连接"
            : $"连接于 {_connectedAt:HH:mm:ss}";
        UpdateUptime();

        foreach (var device in Devices)
        {
            device.IsSelected = device.Id == _receiver.SelectedDeviceId;
            device.IsConnected = IsConnected && device.Id == _receiver.SelectedDeviceId;
        }

        if (!initial)
        {
            LastEventText = $"{status.Title} · {DateTime.Now:HH:mm:ss}";
            _log.Info($"界面状态：{status.State} / {status.Title}");
        }

        if (status.State == ReceiverLinkState.Connected)
        {
            _powerService.SetKeepAwake(KeepAwake);
        }
        else
        {
            _powerService.SetKeepAwake(false);
        }

        if (status.ReconnectCount == 0)
        {
            ReconnectText = status.State == ReceiverLinkState.Connected
                ? "本次连接未重连"
                : "等待首次连接";
        }

        if (status.State == ReceiverLinkState.Connected && previous != ReceiverLinkState.Connected)
        {
            _settings.LastConnectedDeviceId = _receiver.SelectedDeviceId;
            _settings.LastConnectedDeviceName = _receiver.SelectedDeviceName;
            SaveSettings();
        }
    }

    private void ReconnectCountText(int count)
    {
        _reconnectCount = count;
        ReconnectText = count == 0 ? "等待首次连接" : $"已自动重连 {count} 次";
    }

    private void OnReceiverNotification(object? sender, string message)
    {
        if (!ShowNotifications)
        {
            return;
        }

        var warning = message.Contains("断开", StringComparison.Ordinal) ||
                      message.Contains("失败", StringComparison.Ordinal);
        NotificationRequested?.Invoke(this, new TrayNotification("BlueHarbor", message, warning));
    }

    private void UpdateUptime()
    {
        if (_connectedAt is null)
        {
            UptimeText = "--:--:--";
            return;
        }

        var elapsed = DateTime.Now - _connectedAt.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        UptimeText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private async Task LoadAdapterInfoAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            var parts = new List<string>();
            if (bluetoothRadio is not null)
            {
                parts.Add(bluetoothRadio.Name);
                parts.Add(bluetoothRadio.State switch
                {
                    RadioState.On => "已开启",
                    RadioState.Off => "已关闭",
                    _ => "状态未知"
                });
            }
            else
            {
                parts.Add("未检测到蓝牙无线电");
            }

            if (adapter is not null)
            {
                parts.Add(FormatBluetoothAddress(adapter.BluetoothAddress));
            }

            AdapterText = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            _log.Warn($"读取蓝牙适配器信息失败：{ex.Message}");
            AdapterText = "蓝牙适配器信息不可用";
        }
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        if (address == 0)
        {
            return "地址未知";
        }

        var bytes = BitConverter.GetBytes(address).Reverse().ToArray();
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"打开蓝牙设置失败：{ex.Message}");
        }
    }

    private void ShowQualityInfo()
    {
        MessageBox.Show(
            "Windows 原生 A2DP 接收的音质由系统蓝牙栈、蓝牙适配器和手机三方协商决定：\n\n" +
            "• SBC：所有设备都支持的基线编码。\n" +
            "• AAC：Windows 11 原生支持，但部分手机/适配器会出现“已连接但无声”，因此 BlueHarbor 默认使用 SBC；AAC 仅作为实验选项。\n" +
            "• aptX：取决于蓝牙适配器驱动，部分适配器在接收方向可用。\n" +
            "• LDAC / aptX HD：Windows 没有原生 A2DP 接收实现，应用层无法启用；需要厂商驱动或专用硬件。\n\n" +
            "音频输出必须选对：BlueHarbor 使用 Windows 当前默认播放设备。如果默认设备是未接耳机的 USB 耳机/HDMI，手机显示已连接也不会有声音。请在“输出设备”里选择实际在用的扬声器；选择后 BlueHarbor 会把它设为系统默认并自动重连接口。\n\n" +
            "Windows 没有逐连接的编码选择 API。SBC 是默认的稳定模式；只有在确认设备兼容时才启用 AAC 实验模式，出现无声时点“回退 SBC”。\n\n" +
            "BlueHarbor 不会重新编码或压缩音频，只负责让接收端点稳定常驻。实际编码可在手机的开发者选项 / 蓝牙音频编解码器中查看。\n\n" +
            "提升音质的实用建议：\n" +
            "1. 在 Windows 蓝牙设备属性中取消“免提电话”服务，避免 HFP 抢占。\n" +
            "2. 使用支持 AAC 的手机和蓝牙适配器；aptX 还要求适配器驱动在接收方向开放。\n" +
            "3. 关闭系统音效/空间音频等二次处理。",
            "音质与兼容性",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _log.DirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"打开日志目录失败：{ex.Message}");
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            var lines = new List<string>
            {
                AppVersionText,
                $"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
                $"Runtime: {RuntimeInformation.FrameworkDescription}",
                $"Adapter: {AdapterText}",
                $"State: {LinkState} / {StatusTitle}",
                $"Selected: {_receiver.SelectedDeviceName ?? "(none)"}",
                $"Reconnects: {_reconnectCount}",
                $"Devices: {string.Join(", ", Devices.Select(d => d.Name))}",
                $"Log: {_log.DirectoryPath}",
                string.Empty,
                "Recent events:"
            };
            lines.AddRange(_log.RecentLines.TakeLast(30));
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            LastEventText = $"诊断信息已复制 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _log.Warn($"复制诊断信息失败：{ex.Message}");
        }
    }

    public void SaveSettings()
    {
        _settings.SelectedDeviceId = _receiver.SelectedDeviceId;
        _settings.SelectedDeviceName = _receiver.SelectedDeviceName;
        _settings.Armed = _receiver.IsArmed;
        if (_settings.LastConnectedDeviceId is null && _receiver.SelectedDeviceId is not null)
        {
            _settings.LastConnectedDeviceId = _receiver.SelectedDeviceId;
            _settings.LastConnectedDeviceName = _receiver.SelectedDeviceName;
        }
        _settings.AutoReconnect = AutoReconnect;
        _settings.KeepAwake = KeepAwake;
        _settings.AutoStart = AutoStart;
        _settings.CloseToTray = CloseToTray;
        _settings.ShowNotifications = ShowNotifications;
        _settings.StartMinimized = StartMinimized;
        _settingsService.Save(_settings);
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _uptimeTimer.Stop();
        SaveSettings();
        _powerService.Release();
        _receiver.DevicesChanged -= OnDevicesChanged;
        _receiver.StatusChanged -= OnStatusChanged;
        _receiver.NotificationRequested -= OnReceiverNotification;
        await _receiver.ShutdownAsync();
        _settings.CleanShutdown = true;
        _settingsService.Save(_settings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _uptimeTimer.Stop();
        SaveSettings();
        _powerService.Release();
        _receiver.DevicesChanged -= OnDevicesChanged;
        _receiver.StatusChanged -= OnStatusChanged;
        _receiver.NotificationRequested -= OnReceiverNotification;
        _receiver.Dispose();
        _settings.CleanShutdown = true;
        _settingsService.Save(_settings);
    }
}
