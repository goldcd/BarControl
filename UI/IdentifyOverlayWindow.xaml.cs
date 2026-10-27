using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BarControl.Core.Display;
using BarControl.Core.Native;

namespace BarControl.UI;

public partial class IdentifyOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public string IndexNumber { get; set; } = "1";
    public string DisplayTitle { get; set; } = "Display";
    public string ResolutionInfo { get; set; } = "";
    public string StatusText { get; set; } = "ALLOWED";

    public Brush AccentBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
    public Brush CardBorderBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 255, 255));
    public Brush StatusBgBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));

    private readonly DisplayInfo _display;

    public IdentifyOverlayWindow(DisplayInfo display)
    {
        InitializeComponent();
        _display = display;

        IndexNumber = display.Index.ToString();
        DisplayTitle = display.DisplayTitle;
        ResolutionInfo = $"{display.Width} × {display.Height} (at {display.Left}, {display.Top})";

        if (display.IsTargetPrimary)
        {
            StatusText = "PRIMARY TARGET (ALLOWED)";
            AccentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Emerald Green
            StatusBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
            CardBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 16, 185, 129));
        }
        else if (display.IsExcluded)
        {
            StatusText = "NOT IN USE (OFF-LIMITS)";
            AccentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // Crimson Red
            StatusBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
            CardBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 239, 68, 68));
        }
        else
        {
            StatusText = "ALLOWED (SECONDARY)";
            AccentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)); // Blue
            StatusBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            CardBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 59, 130, 246));
        }

        DataContext = this;

        // Preliminary logical position
        Left = display.Left;
        Top = display.Top;
        Width = display.Width;
        Height = display.Height;

        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Position precisely onto the physical monitor rectangle using Win32 SetWindowPos
            Win32.SetWindowPos(
                hwnd,
                new IntPtr(-1), // HWND_TOPMOST
                _display.Left,
                _display.Top,
                _display.Width,
                _display.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // Make window click-through so user can continue using the PC without interruption
            long exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {

        // Display for 2.6 seconds, then fade out smoothly
        await Task.Delay(2600);

        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(400))
        };

        fadeOut.Completed += (s, args) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
