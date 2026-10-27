using System;
using System.Linq;
using System.Threading;
using System.Windows;
using BarControl.Core.Audio;
using BarControl.Core.Cec;
using BarControl.Core.Config;
using BarControl.Core.Engine;
using BarControl.Core.Startup;
using BarControl.Core.Tray;
using BarControl.UI;

namespace BarControl;

public partial class App : Application
{
    private const string MutexName = "Global\\BarControl_MonitorGuard_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;

    private WindowRelocationEngine? _engine;
    private TrayIconService? _trayService;
    private AudioKeepAliveService? _audioService;
    private CecService? _cecService;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Keep Windows uninstaller registration version synced with current running app
        StartupService.SyncInstalledAppVersion();

        // Check single-instance mutex
        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "BarControl is already running in your system tray.\nLook for the monitor icon in the bottom-right taskbar.",
                "BarControl - Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize Core Services
        _engine = new WindowRelocationEngine();
        _trayService = new TrayIconService();
        _audioService = new AudioKeepAliveService(startEnabled: true);
        _cecService = new CecService();

        _viewModel = new MainWindowViewModel(_engine, _trayService, _audioService, _cecService);
        _mainWindow = new MainWindow(_viewModel);

        _trayService.OnOpenRequested += ShowMainWindow;
        _trayService.OnExitRequested += ShutdownApplication;

        // Start window relocation engine hooks
        _engine.Start();

        // Start CEC discovery in background
        _ = _cecService.InitializeAsync();

        // Check if started with --minimized flag or configured to start minimized
        bool startMinimized = e.Args.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                              || _viewModel.StartMinimized;

        if (!startMinimized)
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (Dispatcher.CheckAccess())
        {
            ShowMainWindowInternal();
        }
        else
        {
            Dispatcher.InvokeAsync(ShowMainWindowInternal);
        }
    }

    private void ShowMainWindowInternal()
    {
        if (_mainWindow == null) return;

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Focus();
    }


    private void ShutdownApplication()
    {
        _mainWindow?.RequestExplicitExit();
        _cecService?.Dispose();
        _audioService?.Dispose();
        _engine?.Dispose();
        _trayService?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        finally
        {
            _singleInstanceMutex = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cecService?.Dispose();
        _engine?.Dispose();
        _trayService?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        finally
        {
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
