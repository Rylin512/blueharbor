using System.Drawing;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace BlueHarbor.Services;

/// <summary>
/// Direct Shell_NotifyIcon implementation with a stable GUID identity. The
/// WinForms NotifyIcon creates a new hidden window every process start, which
/// lets Windows keep a stale per-executable tray entry after an update.
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_GUID = 0x20;
    private const uint NIF_SHOWTIP = 0x80;

    private const uint NIIF_INFO = 0x01;
    private const uint NIIF_WARNING = 0x02;

    private const int WM_APP = 0x8000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private static readonly Guid TrayGuid = new("F0A7F9B8-3F42-4D0C-9F4A-2C5A1E7B6D31");

    private readonly Logger _log;
    private readonly Icon _icon;
    private readonly string _tooltip;
    private readonly Action _leftClick;
    private readonly Action _rightClick;
    private readonly Action _taskbarRecreated;
    private readonly NativeWindow _window;
    private readonly int _callbackMessage;
    private readonly int _taskbarCreatedMessage;
    private bool _added;
    private bool _usingGuid = true;
    private bool _disposed;

    public NativeTrayIcon(Logger log, Icon icon, string tooltip,
        Action leftClick, Action rightClick, Action taskbarRecreated)
    {
        _log = log;
        _icon = icon;
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _leftClick = leftClick;
        _rightClick = rightClick;
        _taskbarRecreated = taskbarRecreated;
        _callbackMessage = WM_APP + 1;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _window = new NativeWindow();
        _window.MessageReceived += OnWindowMessage;
        _window.CreateHandle(new WinForms.CreateParams
        {
            Caption = "BlueHarborTrayHost"
        });

        Show();
    }

    public bool IsAdded => _added;

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        if (!_added)
        {
            Add();
        }
        else
        {
            Modify();
        }
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NIF_GUID);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    public void ShowBalloon(string title, string message, bool warning = false)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData(NIF_INFO | NIF_ICON | NIF_TIP | (_usingGuid ? NIF_GUID : 0));
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        data.uTimeoutOrVersion = 5000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = CreateData(
            NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP |
            (_usingGuid ? NIF_GUID : 0));

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            var error = Marshal.GetLastWin32Error();
            _log.Warn($"Shell_NotifyIcon(NIM_ADD) 失败，错误码 {error}");

            if (_usingGuid)
            {
                // Some older shells reject GUID icons. Retry with the legacy
                // hWnd/uID identity.
                _usingGuid = false;
                data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
                if (!Shell_NotifyIcon(NIM_ADD, ref data))
                {
                    _log.Warn($"Shell_NotifyIcon 回退也失败，错误码 {Marshal.GetLastWin32Error()}");
                    return;
                }
            }
            else
            {
                return;
            }
        }

        var version = CreateData(_usingGuid ? NIF_GUID : 0);
        version.uTimeoutOrVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, ref version);
        _added = true;
        _log.Info("Shell_NotifyIcon 托盘图标已注册");
    }

    private void Modify()
    {
        var data = CreateData(NIF_ICON | NIF_TIP | NIF_SHOWTIP | (_usingGuid ? NIF_GUID : 0));
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NotifyIconData CreateData(uint flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window.Handle,
            uID = _usingGuid ? 0u : 1u,
            uFlags = flags,
            uCallbackMessage = (uint)_callbackMessage,
            hIcon = _icon.Handle,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = _usingGuid ? TrayGuid : Guid.Empty,
            hBalloonIcon = IntPtr.Zero
        };
    }

    private void OnWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _added = false;
            Show();
            _taskbarRecreated();
            return;
        }

        if (message != _callbackMessage)
        {
            return;
        }

        var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMessage)
        {
            case WM_LBUTTONUP:
            case NIN_SELECT:
            case NIN_KEYSELECT:
                _leftClick();
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                SetForegroundWindow(_window.Handle);
                _rightClick();
                break;
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
            Hide();
            _window.MessageReceived -= OnWindowMessage;
            _window.DestroyHandle();
        }
        catch
        {
            // Ignore.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private sealed class NativeWindow : WinForms.NativeWindow
    {
        public event Action<int, IntPtr, IntPtr>? MessageReceived;

        protected override void WndProc(ref WinForms.Message m)
        {
            base.WndProc(ref m);
            MessageReceived?.Invoke(m.Msg, m.WParam, m.LParam);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
