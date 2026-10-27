using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BarControl.Core.Native;

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
            Icon = CreateSoundbarIcon(true)
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
        _notifyIcon.Icon = CreateSoundbarIcon(active);
        oldIcon?.Dispose();
    }

    private static Icon CreateSoundbarIcon(bool active)
    {
        // 32x32 high-visibility soundbar vector glyph
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var accentColor = active
            ? System.Drawing.Color.FromArgb(52, 211, 153) // Emerald active accent
            : System.Drawing.Color.FromArgb(148, 163, 184); // Muted slate when inactive

        // 1. Upward Acoustic Soundwave Arcs
        using (var wavePen = new Pen(accentColor, 2.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(wavePen, 16f - 5.5f, 14.5f - 5.5f, 11f, 11f, 215f, 110f);
            g.DrawArc(wavePen, 16f - 9.5f, 14.5f - 9.5f, 19f, 19f, 220f, 100f);
        }

        // 2. Sleek Horizontal Soundbar Enclosure (Pill / Rounded Rect)
        using var path = new GraphicsPath();
        float bx = 2f, by = 15f, bw = 28f, bh = 11f, r = 3.5f;
        path.AddArc(bx, by, r * 2, r * 2, 180, 90);
        path.AddArc(bx + bw - r * 2, by, r * 2, r * 2, 270, 90);
        path.AddArc(bx + bw - r * 2, by + bh - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(bx, by + bh - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();

        using (var bodyBrush = new SolidBrush(System.Drawing.Color.FromArgb(245, 24, 26, 32)))
        using (var borderPen = new Pen(System.Drawing.Color.FromArgb(241, 245, 249), 1.8f))
        {
            g.FillPath(bodyBrush, path);
            g.DrawPath(borderPen, path);
        }

        // 3. Left & Right Speaker Drivers + Center Status Bar
        using (var driverBrush = new SolidBrush(System.Drawing.Color.FromArgb(241, 245, 249)))
        {
            g.FillEllipse(driverBrush, 5.2f, 18.3f, 4.4f, 4.4f);
            g.FillEllipse(driverBrush, 22.4f, 18.3f, 4.4f, 4.4f);
        }

        using (var grillePen = new Pen(accentColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(grillePen, 13f, 20.5f, 19f, 20.5f);
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
