using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using BlueHarbor.Models;
using BlueHarbor.Services;
using BlueHarbor.ViewModels;

namespace BlueHarbor;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private Logger? _logger;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _singleInstance = new SingleInstance();
            if (!_singleInstance.IsFirstInstance)
            {
                _singleInstance.SignalFirstInstance();
                Shutdown();
                return;
            }

            var dataDirectory = ResolveDataDirectory();
            _logger = new Logger(Path.Combine(dataDirectory, "logs"));
            _logger.Info("────────────────────────────────────────");
            _logger.Info($"BlueHarbor 启动，参数：{string.Join(' ', e.Args)}");
            _logger.Info($"数据目录：{dataDirectory}");
            _logger.Info($"OS：{Environment.OSVersion} / {Environment.Version}");
            try
            {
                _logger.Info($"Session：{Process.GetCurrentProcess().SessionId}，Interactive={Environment.UserInteractive}");
            }
            catch
            {
                // Diagnostic only.
            }

            var settingsService = new SettingsService(dataDirectory, _logger);
            var settings = settingsService.Load();
            var wasCleanShutdown = settings.CleanShutdown;
            settings.CleanShutdown = false;
            settingsService.Save(settings);

            var startupService = new StartupService(_logger);
            var powerService = new PowerService(_logger);
            var aacPreference = new AacPreferenceService(_logger);
            var receiver = new ReceiverService(_logger, Dispatcher)
            {
                ResetFirstConnectionOnStart = !wasCleanShutdown
            };

            _viewModel = new MainViewModel(
                _logger,
                settings,
                settingsService,
                startupService,
                powerService,
                aacPreference,
                receiver);
            _mainWindow = new MainWindow(_viewModel, _logger, settings);
            MainWindow = _mainWindow;
            _tray = new TrayIconService(_mainWindow, _viewModel, _logger);

            _viewModel.NotificationRequested += (_, notification) =>
                _tray?.ShowBalloon(notification.Title, notification.Message, notification.IsWarning);
            _viewModel.ExitRequested += (_, _) => RequestExit();
            _mainWindow.HiddenToTray += (_, _) =>
                _tray?.ShowBalloon("BlueHarbor 正在后台运行",
                    "接收端会继续工作。双击托盘图标可以随时回到这里。");
            _singleInstance.ShowRequested += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
            SessionEnding += (_, _) =>
            {
                _exiting = true;
                try
                {
                    _viewModel?.Dispose();
                    _tray?.Dispose();
                }
                catch
                {
                    // Windows is ending the session; do not block it further.
                }
            };

            var startMinimized = e.Args.Any(a =>
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/minimized", StringComparison.OrdinalIgnoreCase)) ||
                settings.StartMinimized;

            if (!startMinimized)
            {
                _mainWindow.Show();
            }

            await _viewModel.InitializeAsync();
            try
            {
                var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
                _logger.Info($"启动完成，进程启动至今 {elapsed.TotalMilliseconds:F0} ms");
            }
            catch
            {
                _logger.Info("启动完成");
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write(ex, "启动失败");
            _logger?.Error(ex, "启动失败");
            try
            {
                MessageBox.Show(
                    "BlueHarbor 启动失败。\n\n" +
                    ex.Message +
                    "\n\n诊断日志：\n" + StartupDiagnostics.LogPath +
                    "\n\n可以把该日志发给开发者定位问题。",
                    "BlueHarbor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // If even the message box fails, exit without another exception.
            }

            Shutdown(1);
        }
    }

    /// <summary>
    /// Choose a writable data directory. LocalAppData is preferred, but
    /// portable copies and locked-down launch contexts may not be allowed to
    /// write there; fall back next to the exe, then to %TEMP%.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var errors = new List<string>();
        var candidates = new List<string>();

        var configured = Environment.GetEnvironmentVariable("BLUEHARBOR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(Path.Combine(local, "BlueHarbor"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "BlueHarborData"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "BlueHarbor"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryPrepareDirectory(candidate, out var error))
            {
                return candidate;
            }
            errors.Add($"{candidate} → {error}");
        }

        throw new IOException("无法创建可写的数据目录：\n" + string.Join("\n", errors));
    }

    private static bool TryPrepareDirectory(string path, out string error)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowFromTray();
    }

    public async void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.ShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write(ex, "退出时释放接收端失败");
            _logger?.Warn($"退出时释放接收端失败：{ex.Message}");
        }

        _mainWindow?.PrepareForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        _viewModel?.Dispose();
        _logger?.Info("BlueHarbor 退出");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.Write(e.Exception, "UI 线程未处理异常");
        _logger?.Error(e.Exception, "UI 线程未处理异常");
        e.Handled = true;
        try
        {
            _tray?.ShowBalloon("BlueHarbor", "发生了内部错误，已记录日志。接收端会继续尝试保持连接。", warning: true);
        }
        catch
        {
            // Never let error reporting crash the app.
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception("未知未处理异常");
        StartupDiagnostics.Write(ex, "后台线程未处理异常");
        _logger?.Error(ex, "后台线程未处理异常");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupDiagnostics.Write(e.Exception, "未观察的任务异常");
        _logger?.Error(e.Exception, "未观察的任务异常");
        e.SetObserved();
    }
}
