using System.Text.Json.Serialization;

namespace BlueHarbor.Models;

public sealed class AppSettings
{
    public string? SelectedDeviceId { get; set; }
    public string? SelectedDeviceName { get; set; }
    public string? LastConnectedDeviceId { get; set; }
    public string? LastConnectedDeviceName { get; set; }
    public bool Armed { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool StartMinimized { get; set; }
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 720;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool FirstRunCompleted { get; set; }

    /// <summary>
    /// False means the previous process did not get a chance to release its
    /// A2DP endpoint cleanly. The next launch performs a one-time re-engage.
    /// </summary>
    public bool CleanShutdown { get; set; }

    /// <summary>
    /// Whether the one-time "prefer AAC" prompt has been shown.
    /// </summary>
    public bool AacPromptShown { get; set; }

    [JsonIgnore]
    public bool HasSavedWindowPosition =>
        WindowLeft is not null && WindowTop is not null &&
        WindowLeft > -10000 && WindowTop > -10000;
}
