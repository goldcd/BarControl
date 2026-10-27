using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using BarControl.Core.Native;

namespace BarControl;

public partial class MainWindow : Window
{
    private bool _isExplicitExit = false;

    public UI.MainWindowViewModel ViewModel { get; }

    public MainWindow(UI.MainWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;

        StateChanged += (s, e) =>
        {
            ViewModel.IsUiActive = (WindowState != WindowState.Minimized && IsVisible);
        };
        IsVisibleChanged += (s, e) =>
        {
            ViewModel.IsUiActive = (WindowState != WindowState.Minimized && IsVisible);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Hook window message loop for WM_DISPLAYCHANGE to auto-detect monitor plug/unplug or sleep/wake
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_DISPLAYCHANGE)
        {
            // Windows display resolution or monitor setup changed!
            Dispatcher.InvokeAsync(() =>
            {
                ViewModel.LoadDisplays();
            });
        }
        return IntPtr.Zero;
    }

    public void RequestExplicitExit()
    {
        _isExplicitExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Minimize to system tray instead of exiting process
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private void OnSweepClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SweepNow();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadDisplays();
    }

    private async void OnTogglePowerClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.TogglePowerSoundbarAsync();
    }

    private void OnVolumeSliderThumbDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        ViewModel.OnSliderDragStarted();
    }

    private async void OnVolumeSliderThumbDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        await ViewModel.OnSliderDragEndedAsync();
    }

    private void OnVolumeSliderMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewModel.OnSliderDragStarted();
    }

    private async void OnVolumeSliderMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await ViewModel.OnSliderDragEndedAsync();
    }

    private async void OnVolumeUpClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.StepVolumeUpAsync();
    }

    private async void OnVolumeDownClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.StepVolumeDownAsync();
    }
}
