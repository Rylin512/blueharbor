using BlueHarbor.Models;

namespace BlueHarbor.ViewModels;

public sealed class DeviceItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isConnected;

    public DeviceItemViewModel(ReceiverDevice device, bool isSelected, bool isConnected)
    {
        Id = device.Id;
        Name = device.Name;
        Address = device.ShortAddress;
        _isSelected = isSelected;
        _isConnected = isConnected;
    }

    public string Id { get; }
    public string Name { get; }
    public string Address { get; }

    public string Initial
    {
        get
        {
            var text = Name.Trim();
            return text.Length == 0 ? "?" : text[..1].ToUpperInvariant();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(Badge));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsCurrent));
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(Badge));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsCurrent));
            }
        }
    }

    public bool IsCurrent => IsSelected;
    public bool ShowIdleBadge => !IsSelected;

    public string Badge => IsConnected ? "正在接收" : IsSelected ? "当前选择" : "已配对";
    public string Subtitle => IsConnected
        ? "音频通道已建立"
        : IsSelected
            ? "开启接收后等待此设备"
            : "点击切换到这台设备";

    public string AccessibilityName => $"{Name}，{Badge}";
}
