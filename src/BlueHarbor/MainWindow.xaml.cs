using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BlueHarbor.Models;
using BlueHarbor.Services;
using BlueHarbor.ViewModels;
using Application = System.Windows.Application;

namespace BlueHarbor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Logger _log;
    private readonly AppSettings _settings;
    private bool _allowClose;
    private bool _trayHintShown;

    public MainWindow(MainViewModel viewModel, Logger log, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _log = log;
        _settings = settings;
        DataContext = viewModel;

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.HasSavedWindowPosition &&
            IsPositionVisible(settings.WindowLeft!.Value, settings.WindowTop!.Value))
        {
            Left = settings.WindowLeft.Value;
            Top = settings.WindowTop.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        SourceInitialized += (_, _) => ApplyNativeWindowStyle();
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Loaded += (_, _) => ContentScroll.ScrollToTop();
    }

    public event EventHandler? HiddenToTray;

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void HideToTray()
    {
        SaveWindowBounds();
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RequestExit()
    {
        if (Application.Current is App app)
        {
            app.RequestExit();
            return;
        }

        PrepareForExit();
        Application.Current.Shutdown();
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        SaveWindowBounds();
        // MainViewModel.ShutdownAsync already saved settings before the
        // receiver was released. Saving again here would overwrite Armed with
        // false and break auto-arm on the next launch.
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _viewModel.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!_allowClose)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(RequestExit));
            return;
        }

        SaveWindowBounds();
        if (!_allowClose)
        {
            _viewModel.SaveSettings();
        }
        base.OnClosing(e);
    }

    private void SaveWindowBounds()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            if (bounds.Width < 900 || bounds.Height < 620)
            {
                return;
            }

            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }
        catch (Exception ex)
        {
            _log.Warn($"保存窗口位置失败：{ex.Message}");
        }
    }

    private static bool IsPositionVisible(double left, double top)
    {
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return false;
        }

        return left >= SystemParameters.VirtualScreenLeft - 50 &&
               top >= SystemParameters.VirtualScreenTop - 50 &&
               left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
               top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse was released between the event and DragMove.
            }
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph is null)
        {
            return;
        }

        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private void ApplyNativeWindowStyle()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch (Exception ex)
        {
            _log.Warn($"设置窗口外观失败：{ex.Message}");
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
