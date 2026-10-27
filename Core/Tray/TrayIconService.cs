using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BarControl.Core.Native;
using BarControl.Models;

namespace BarControl.Core.Tray;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private bool _isDisposed;

    public event Action? OnOpenRequested;
    public event Action? OnExitRequested;

    public TrayIconService()
    {
        _contextMenu = new ContextMenuStrip();

        // 1. Launch GUI option
        var openItem = new ToolStripMenuItem("Open BarControl", null, (s, e) => OnOpenRequested?.Invoke())
        {
            Font = new Font(_contextMenu.Font ?? SystemFonts.DefaultFont, FontStyle.Bold)
        };

        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => OnExitRequested?.Invoke());

        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "BarControl",
            Visible = true,
            ContextMenuStrip = _contextMenu,
            Icon = CreateMonitorIcon(true)
        };

        // Single left-click directly opens the application GUI.
        // Right-click automatically opens the ContextMenuStrip.
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OnOpenRequested?.Invoke();
            }
        };

        _notifyIcon.DoubleClick += (s, e) => OnOpenRequested?.Invoke();
    }


    public void SetGuardState(bool isEnabled)
    {
        UpdateIconState(isEnabled);
        _notifyIcon.Text = "BarControl";
    }

    private void UpdateIconState(bool active)
    {
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = CreateMonitorIcon(active);
        oldIcon?.Dispose();
    }

    public void ShowRelocationNotification(RelocationLogItem item)
    {
        // Suppressed per user request: no balloon notifications on window relocation
    }

    private static Icon CreateMonitorIcon(bool withForbiddenOverlay)
    {
        // 32x32 high-visibility vector glyph
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 1. Minimalist Monitor Glyph
        using var monitorPen = new Pen(System.Drawing.Color.FromArgb(241, 245, 249), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        
        using var screenBrush = new SolidBrush(System.Drawing.Color.FromArgb(220, 24, 26, 32));
        g.FillRectangle(screenBrush, 4, 5, 24, 15);
        g.DrawRectangle(monitorPen, 3, 4, 25, 16);

        // Stand neck & foot
        g.DrawLine(monitorPen, 16, 20, 16, 25);
        g.DrawLine(monitorPen, 9, 25, 23, 25);

        if (withForbiddenOverlay)
        {
            // Option 4: Bold Diagonal Ban Stripe across monitor (bottom-left to top-right)
            using var haloPen = new Pen(System.Drawing.Color.FromArgb(230, 15, 17, 23), 5.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(haloPen, 3f, 25f, 29f, 3f);

            // Vivid crimson red stripe
            using var redPen = new Pen(System.Drawing.Color.FromArgb(239, 68, 68), 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(redPen, 3f, 25f, 29f, 3f);
        }
        else
        {
            // When guard paused: clean monitor with subtle power dot on bezel
            using var dotBrush = new SolidBrush(System.Drawing.Color.FromArgb(148, 163, 184));
            g.FillEllipse(dotBrush, 15, 17, 2, 2);
        }

        IntPtr hIcon = bmp.GetHicon();
        using var tempIcon = Icon.FromHandle(hIcon);
        var icon = (Icon)tempIcon.Clone();

        // Release unmanaged GDI icon handle to prevent leaks
        Win32.DestroyIcon(hIcon);

        return icon;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _isDisposed = true;
        }
    }
}
