using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using BlueHarbor.ViewModels;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace BlueHarbor.Services;

public sealed class TrayIconService : IDisposable
{
    private const string TrayText = "BlueHarbor 蓝牙音频接收器";
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Logger _log;
    private readonly NativeTrayIcon _tray;
    private readonly WinForms.NotifyIcon? _fallbackNotifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private readonly WinForms.ToolStripMenuItem _keepAwakeItem;
    private readonly Icon? _icon;
    private bool _disposed;

    public TrayIconService(MainWindow window, MainViewModel viewModel, Logger log)
    {
        _window = window;
        _viewModel = viewModel;
        _log = log;

        _icon = LoadIcon();
        _menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = Color.FromArgb(15, 23, 36),
            ForeColor = Color.FromArgb(232, 240, 251),
            Renderer = new DarkMenuRenderer(),
            Font = new Font("Microsoft YaHei UI", 9f),
            Padding = new WinForms.Padding(4, 4, 4, 4)
        };

        _statusItem = new WinForms.ToolStripMenuItem("接收已暂停")
        {
            Enabled = false,
            ForeColor = Color.FromArgb(140, 161, 190)
        };
        _toggleItem = new WinForms.ToolStripMenuItem("开启接收");
        _autoStartItem = new WinForms.ToolStripMenuItem("开机启动")
        {
            CheckOnClick = false
        };
        _keepAwakeItem = new WinForms.ToolStripMenuItem("连接时保持系统唤醒")
        {
            CheckOnClick = false
        };
        var showItem = new WinForms.ToolStripMenuItem("显示主界面");
        var exitItem = new WinForms.ToolStripMenuItem("退出 BlueHarbor");

        _toggleItem.Click += async (_, _) => await ToggleAsync();
        _autoStartItem.Click += (_, _) =>
        {
            _viewModel.AutoStart = !_viewModel.AutoStart;
            SyncMenu();
        };
        _keepAwakeItem.Click += (_, _) =>
        {
            _viewModel.KeepAwake = !_viewModel.KeepAwake;
            SyncMenu();
        };
        showItem.Click += (_, _) => _window.ShowFromTray();
        exitItem.Click += (_, _) => _window.RequestExit();

        _menu.Items.Add(showItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(_keepAwakeItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => SyncMenu();

        _tray = new NativeTrayIcon(
            _log,
            _icon ?? SystemIcons.Application,
            TrayText,
            () => _window.ShowFromTray(),
            ShowContextMenu,
            () => SyncMenu());
        _tray.Show();

        if (!_tray.IsAdded)
        {
            // If the native shell call is unavailable (for example a
            // restricted desktop), keep the WinForms implementation as a
            // second chance. It uses a different shell path.
            _fallbackNotifyIcon = new WinForms.NotifyIcon
            {
                Icon = _icon ?? SystemIcons.Application,
                Text = TrayText,
                Visible = true,
                ContextMenuStrip = _menu
            };
            _fallbackNotifyIcon.DoubleClick += (_, _) => _window.ShowFromTray();
            _fallbackNotifyIcon.MouseClick += (_, args) =>
            {
                if (args.Button == WinForms.MouseButtons.Left)
                {
                    _window.ShowFromTray();
                }
            };
            _log.Warn("原生托盘注册失败，已启用 WinForms 托盘回退");
        }

        SyncMenu();
        _log.Info("系统托盘图标已创建");
        _ = PromoteTrayIconAsync();
    }

    /// <summary>
    /// Windows 11 hides new notification icons in the overflow area by
    /// default. It also keeps stale per-executable entries after the app is
    /// moved or updated. Repair the entry for the current executable, mark it
    /// promoted, and re-add the icon so the setting takes effect.
    /// </summary>
    private async Task PromoteTrayIconAsync()
    {
        try
        {
            await Task.Delay(1200);
            if (_disposed)
            {
                return;
            }

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe))
            {
                return;
            }

            using var root = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", writable: true);
            if (root is null)
            {
                return;
            }

            var updated = false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var item = root.OpenSubKey(name, writable: true);
                if (item is null)
                {
                    continue;
                }

                if (item.GetValue("ExecutablePath") is not string path ||
                    !path.Contains("BlueHarbor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(path, currentExe, StringComparison.OrdinalIgnoreCase))
                {
                    item.SetValue("ExecutablePath", currentExe, RegistryValueKind.String);
                    item.SetValue("InitialTooltip", TrayText, RegistryValueKind.String);
                    updated = true;
                }

                var promoted = item.GetValue("IsPromoted") is int value && value != 0;
                if (!promoted)
                {
                    item.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    updated = true;
                }
            }

            if (!updated)
            {
                return;
            }

            // Force the shell to re-read the settings for this icon.
            if (_fallbackNotifyIcon is not null)
            {
                _fallbackNotifyIcon.Visible = false;
                await Task.Delay(150);
                if (!_disposed)
                {
                    _fallbackNotifyIcon.Visible = true;
                }
            }
            else
            {
                _tray.Hide();
                await Task.Delay(150);
                if (!_disposed)
                {
                    _tray.Show();
                }
            }

            _log.Info("已修复并固定 Windows 11 托盘图标设置");
        }
        catch (Exception ex)
        {
            _log.Warn($"固定托盘图标失败：{ex.Message}");
        }
    }

    private async Task ToggleAsync()
    {
        if (_viewModel.ToggleArmCommand.CanExecute(null))
        {
            _viewModel.ToggleArmCommand.Execute(null);
        }
        await Task.CompletedTask;
    }

    private void SyncMenu()
    {
        _statusItem.Text = _viewModel.StatusTitle;
        _statusItem.ForeColor = _viewModel.LinkState switch
        {
            ReceiverLinkState.Connected => Color.FromArgb(77, 227, 193),
            ReceiverLinkState.Error => Color.FromArgb(255, 107, 107),
            _ => Color.FromArgb(140, 161, 190)
        };
        _toggleItem.Text = _viewModel.IsArmed ? "暂停接收" : "开启接收";
        _autoStartItem.Checked = _viewModel.AutoStart;
        _keepAwakeItem.Checked = _viewModel.KeepAwake;

    }

    private void ShowContextMenu()
    {
        try
        {
            _menu.Show(WinForms.Cursor.Position);
        }
        catch (Exception ex)
        {
            _log.Warn($"显示托盘菜单失败：{ex.Message}");
        }
    }

    public void ShowBalloon(string title, string message, bool warning = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_fallbackNotifyIcon is not null)
            {
                _fallbackNotifyIcon.BalloonTipTitle = title;
                _fallbackNotifyIcon.BalloonTipText = message;
                _fallbackNotifyIcon.BalloonTipIcon = warning
                    ? WinForms.ToolTipIcon.Warning
                    : WinForms.ToolTipIcon.Info;
                _fallbackNotifyIcon.ShowBalloonTip(3500);
            }
            else
            {
                _tray.ShowBalloon(title, message, warning);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"显示托盘通知失败：{ex.Message}");
        }
    }

    private static Icon? LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/TrayIcon.ico"));
            if (resource?.Stream is null)
            {
                return null;
            }

            using var stream = resource.Stream;
            var size = WinForms.SystemInformation.SmallIconSize;
            return new Icon(stream, size.Width, size.Height);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _tray.Dispose();
        }
        catch
        {
            // Ignore.
        }
        try
        {
            if (_fallbackNotifyIcon is not null)
            {
                _fallbackNotifyIcon.Visible = false;
                _fallbackNotifyIcon.Dispose();
            }
        }
        catch
        {
            // Ignore.
        }
        _menu.Dispose();
        _icon?.Dispose();
    }

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            using var brush = new SolidBrush(Color.FromArgb(28, 45, 64));
            e.Graphics.FillRectangle(brush, new Rectangle(System.Drawing.Point.Empty, e.Item.Size));
        }
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(15, 23, 36);
        public override Color MenuBorder => Color.FromArgb(38, 56, 78);
        public override Color MenuItemBorder => Color.FromArgb(45, 72, 92);
        public override Color MenuItemSelected => Color.FromArgb(28, 45, 64);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(28, 45, 64);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(28, 45, 64);
        public override Color ImageMarginGradientBegin => Color.FromArgb(15, 23, 36);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(15, 23, 36);
        public override Color ImageMarginGradientEnd => Color.FromArgb(15, 23, 36);
        public override Color SeparatorDark => Color.FromArgb(38, 56, 78);
        public override Color SeparatorLight => Color.FromArgb(15, 23, 36);
        public override Color CheckBackground => Color.FromArgb(28, 45, 64);
        public override Color CheckSelectedBackground => Color.FromArgb(35, 60, 78);
        public override Color CheckPressedBackground => Color.FromArgb(35, 60, 78);
    }
}
