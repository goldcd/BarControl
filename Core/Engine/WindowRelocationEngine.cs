using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using Microsoft.Win32;
using BarControl.Core.Display;
using BarControl.Core.Native;
using BarControl.Models;

namespace BarControl.Core.Engine;

public class WindowRelocationEngine : IDisposable
{
    private readonly uint _currentProcessId;
    private readonly ConcurrentDictionary<IntPtr, DateTime> _recentlyRelocated = new();

    // Cache of last known safe position & size of windows on allowed monitors (Requirement 3)
    private readonly ConcurrentDictionary<IntPtr, Win32.RECT> _lastKnownSafeBounds = new();
    private readonly ConcurrentDictionary<IntPtr, bool> _lastKnownMaximized = new();

    private readonly List<IntPtr> _hookHandles = new();
    private Win32.WinEventDelegate? _winEventDelegate;
    private System.Timers.Timer? _sweepTimer;

    private readonly object _lock = new();
    private List<DisplayInfo> _monitors = new();
    private bool _isGuardEnabled = true;
    private int _sweepIntervalMs = 1000;
    private volatile bool _isSessionLocked;
    private bool _isSessionHooked;

    public event Action<RelocationLogItem>? OnWindowRelocated;

    public WindowRelocationEngine()
    {
        _currentProcessId = (uint)Process.GetCurrentProcess().Id;
    }

    public void Start()
    {
        lock (_lock)
        {
            Stop();

            _winEventDelegate = HookCallback;

            if (!_isSessionHooked)
            {
                SystemEvents.SessionSwitch += HandleSessionSwitch;
                _isSessionHooked = true;
            }

            // 1. Hook window drag/resize completion (mouse drop event)
            IntPtr hookMoveEnd = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_MOVESIZEEND,
                Win32.EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);

            // 2. Hook window location change (captures keyboard moves like Shift+Win+Arrow instantly)
            IntPtr hookLocation = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);

            // 3. Hook window foreground change
            IntPtr hookForeground = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND,
                Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);

            // 4. Hook new window shown
            IntPtr hookShow = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_SHOW,
                Win32.EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);

            if (hookMoveEnd != IntPtr.Zero) _hookHandles.Add(hookMoveEnd);
            if (hookLocation != IntPtr.Zero) _hookHandles.Add(hookLocation);
            if (hookForeground != IntPtr.Zero) _hookHandles.Add(hookForeground);
            if (hookShow != IntPtr.Zero) _hookHandles.Add(hookShow);

            // Start safeguard sweep timer
            if (_sweepIntervalMs > 0)
            {
                _sweepTimer = new System.Timers.Timer(_sweepIntervalMs);
                _sweepTimer.Elapsed += (s, e) => SweepAllWindows();
                _sweepTimer.AutoReset = true;
                _sweepTimer.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_sweepTimer != null)
            {
                _sweepTimer.Stop();
                _sweepTimer.Dispose();
                _sweepTimer = null;
            }

            if (_isSessionHooked)
            {
                SystemEvents.SessionSwitch -= HandleSessionSwitch;
                _isSessionHooked = false;
            }

            foreach (var hook in _hookHandles)
            {
                if (hook != IntPtr.Zero)
                {
                    Win32.UnhookWinEvent(hook);
                }
            }
            _hookHandles.Clear();
            _winEventDelegate = null;
        }
    }

    private void HandleSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _isSessionLocked = true;
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _isSessionLocked = false;
            // Allow Windows a moment to tear down its lock UI before sweeping
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (!_isSessionLocked && _isGuardEnabled)
                {
                    SweepAllWindows();
                }
            });
        }
    }

    public void UpdateConfiguration(List<DisplayInfo> monitors, bool isGuardEnabled, int sweepIntervalMs)
    {
        lock (_lock)
        {
            _monitors = monitors.ToList();
            _isGuardEnabled = isGuardEnabled;

            if (_sweepIntervalMs != sweepIntervalMs)
            {
                _sweepIntervalMs = sweepIntervalMs;
                if (_sweepTimer != null)
                {
                    _sweepTimer.Interval = Math.Max(250, _sweepIntervalMs);
                }
            }
        }
    }

    public void SweepAllWindows()
    {
        if (!_isGuardEnabled || _isSessionLocked || Win32.IsWorkstationLocked()) return;

        Win32.EnumWindows((hWnd, lParam) =>
        {
            CheckAndRelocateWindow(hWnd);
            return true;
        }, IntPtr.Zero);
    }

    private void HookCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!_isGuardEnabled || hwnd == IntPtr.Zero || _isSessionLocked || Win32.IsWorkstationLocked()) return;

        // idObject: 0 = OBJID_WINDOW
        if (idObject != 0) return;

        if (eventType == Win32.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // For location change events, only inspect active foreground window to keep 0% CPU
            IntPtr fg = Win32.GetForegroundWindow();
            if (hwnd != fg) return;
        }

        CheckAndRelocateWindow(hwnd);
    }

    public bool CheckAndRelocateWindow(IntPtr hWnd)
    {
        if (!_isGuardEnabled || hWnd == IntPtr.Zero || _isSessionLocked || Win32.IsWorkstationLocked()) return false;

        // Debounce: don't process the same window repeatedly within 350ms
        if (_recentlyRelocated.TryGetValue(hWnd, out var lastTime))
        {
            if ((DateTime.UtcNow - lastTime).TotalMilliseconds < 350)
            {
                return false;
            }
        }

        // Fast validation: must be a valid, visible, non-minimized window
        if (!Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
        {
            return false;
        }

        // Check process ID
        Win32.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0) return false;

        string processName = GetProcessName(processId);

        // Exclude Windows Snipping Tool, Screen Capture, Game Bar, and Shell Overlays
        if (processName.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("QuickSnip", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShareX", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Greenshot", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Lightshot", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("WindowsInternal.ComposableShell.Experiences.Lockapp", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("CredentialUIBroker", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Magnify", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Protect BarControl's own windows from relocation
        if (processId == _currentProcessId)
        {
            long exStyleFlag = (long)Win32.GetWindowLongPtr(hWnd, Win32.GWL_EXSTYLE);
            if ((exStyleFlag & (Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT)) != 0)
            {
                return false;
            }
        }


        // Ignore desktop and shell windows
        if (hWnd == Win32.GetDesktopWindow() || hWnd == Win32.GetShellWindow())
        {
            return false;
        }

        // Check window style
        long style = (long)Win32.GetWindowLongPtr(hWnd, Win32.GWL_STYLE);
        long exStyle = (long)Win32.GetWindowLongPtr(hWnd, Win32.GWL_EXSTYLE);

        // Ignore child windows
        if ((style & Win32.WS_CHILD) != 0)
        {
            return false;
        }

        // Ignore pure toolwindows (floating toolbars, tooltips)
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0 && (style & Win32.WS_POPUP) != 0)
        {
            return false;
        }

        // Check class name for known shell elements to avoid moving
        var classNameBuilder = new StringBuilder(256);
        Win32.GetClassName(hWnd, classNameBuilder, 256);
        string className = classNameBuilder.ToString();

        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "WorkerW" or "Progman" or
            "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "DV2ControlHost" or
            "ScreenClippingHost" or "Microsoft.Windows.SnippingTool")
        {
            return false;
        }

        // Get window actual rectangle
        if (!Win32.GetWindowActualRect(hWnd, out Win32.RECT rect) || rect.Width <= 10 || rect.Height <= 10)
        {
            return false;
        }

        // Multi-monitor virtual screen check:
        // Screen capture overlays (Snipping Tool, ShareX, etc.) create a canvas that spans across all monitors.
        // Never touch or relocate any window that spans the full multi-monitor virtual screen!
        int vWidth = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vHeight = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (vWidth > 0 && vHeight > 0 && rect.Width >= vWidth * 0.90 && rect.Height >= vHeight * 0.90)
        {
            return false;
        }

        List<DisplayInfo> activeMonitors;
        lock (_lock)
        {
            activeMonitors = _monitors.ToList();
        }

        if (activeMonitors.Count == 0) return false;

        // Target primary monitor: must NOT be excluded
        var targetPrimary = activeMonitors.FirstOrDefault(m => m.IsTargetPrimary && !m.IsExcluded)
                            ?? activeMonitors.FirstOrDefault(m => m.IsOsPrimary && !m.IsExcluded)
                            ?? activeMonitors.FirstOrDefault(m => !m.IsExcluded);

        if (targetPrimary == null) return false;

        //Excluded monitors can NEVER include the target primary or OS primary monitor!
        var excludedMonitors = activeMonitors
            .Where(m => m.IsExcluded && !m.IsOsPrimary && !m.IsTargetPrimary && m.HMonitor != targetPrimary.HMonitor && m.DeviceName != targetPrimary.DeviceName)
            .ToList();

        if (excludedMonitors.Count == 0) return false;

        // Dominance & Overlap Check:
        // A window is only a candidate for relocation if its presence on an excluded monitor is strictly dominant!
        // If it's already mostly on the primary monitor, leave it alone.
        double primaryOverlap = targetPrimary.OverlapArea(rect.Left, rect.Top, rect.Right, rect.Bottom);
        double maxExcludedOverlap = 0;
        DisplayInfo? sourceExcludedMonitor = null;

        foreach (var exc in excludedMonitors)
        {
            double overlap = exc.OverlapArea(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (overlap > maxExcludedOverlap)
            {
                maxExcludedOverlap = overlap;
                sourceExcludedMonitor = exc;
            }
        }

        double windowArea = (double)rect.Width * rect.Height;

        // If the window is not meaningfully on an excluded monitor (>25% area), or if it is primarily on the target primary, ignore!
        if (sourceExcludedMonitor == null || maxExcludedOverlap <= 0 || (maxExcludedOverlap / windowArea) < 0.25 || primaryOverlap >= maxExcludedOverlap)
        {
            // Check fallback for windows centered entirely in an excluded display without primary overlap
            int centerX = rect.Left + rect.Width / 2;
            int centerY = rect.Top + rect.Height / 2;
            var centerExcluded = excludedMonitors.FirstOrDefault(m => m.ContainsPoint(centerX, centerY));

            if (centerExcluded != null && primaryOverlap <= 0)
            {
                sourceExcludedMonitor = centerExcluded;
            }
            else
            {
                sourceExcludedMonitor = null;
            }
        }

        // Requirement 3: If window is on a safe/allowed monitor, RECORD its position and size!
        if (sourceExcludedMonitor == null)
        {
            bool isCurrentZoomed = Win32.IsZoomed(hWnd);
            if (!isCurrentZoomed && Win32.GetWindowRect(hWnd, out var safeWinRect) && safeWinRect.Width >= 100 && safeWinRect.Height >= 100)
            {
                _lastKnownSafeBounds[hWnd] = safeWinRect;
                _lastKnownMaximized[hWnd] = false;
            }
            else if (isCurrentZoomed)
            {
                _lastKnownMaximized[hWnd] = true;
            }

            if (_lastKnownSafeBounds.Count > 200)
            {
                var deadHandles = _lastKnownSafeBounds.Keys.Where(k => !Win32.IsWindow(k)).ToList();
                foreach (var k in deadHandles)
                {
                    _lastKnownSafeBounds.TryRemove(k, out _);
                    _lastKnownMaximized.TryRemove(k, out _);
                }
            }
            return false;
        }

        // Sanity Check: Never relocate to the same monitor!
        if (sourceExcludedMonitor.HMonitor == targetPrimary.HMonitor ||
            sourceExcludedMonitor.DeviceName == targetPrimary.DeviceName)
        {
            return false;
        }

        // RELOCATE to Target Primary!
        _recentlyRelocated[hWnd] = DateTime.UtcNow;

        // Clean old cache entries
        if (_recentlyRelocated.Count > 100)
        {
            var expired = _recentlyRelocated.Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > 10).Select(k => k.Key).ToList();
            foreach (var key in expired) _recentlyRelocated.TryRemove(key, out _);
        }

        // Get window title for logging
        string windowTitle = GetWindowTitle(hWnd);

        bool isZoomed = Win32.IsZoomed(hWnd);

        // Requirement 3: Restore previous position and dimensions if available!
        bool hadSavedBounds = _lastKnownSafeBounds.TryGetValue(hWnd, out var savedRect);
        bool wasMaximized = (_lastKnownMaximized.TryGetValue(hWnd, out var wasMax) && wasMax) || isZoomed;

        Win32.GetWindowRect(hWnd, out var curWinRect);
        int curWidth = curWinRect.Width;
        int curHeight = curWinRect.Height;

        int targetWidth, targetHeight, targetX, targetY;
        bool resizeNeeded = false;

        if (hadSavedBounds && savedRect.Width >= 100 && savedRect.Height >= 100)
        {
            // Restore exact previous width, height, and coordinates on the primary monitor!
            targetWidth = Math.Min(savedRect.Width, targetPrimary.WorkWidth);
            targetHeight = Math.Min(savedRect.Height, targetPrimary.WorkHeight);
            targetX = Math.Max(targetPrimary.WorkLeft, Math.Min(savedRect.Left, targetPrimary.WorkRight - targetWidth));
            targetY = Math.Max(targetPrimary.WorkTop, Math.Min(savedRect.Top, targetPrimary.WorkBottom - targetHeight));
            resizeNeeded = (targetWidth != curWidth || targetHeight != curHeight);
        }
        else
        {
            // Fallback for new window launching directly onto excluded monitor:
            targetWidth = Math.Min(curWidth > 0 ? curWidth : rect.Width, targetPrimary.WorkWidth);
            targetHeight = Math.Min(curHeight > 0 ? curHeight : rect.Height, targetPrimary.WorkHeight);
            targetX = targetPrimary.WorkLeft + Math.Max(0, (targetPrimary.WorkWidth - targetWidth) / 2);
            targetY = targetPrimary.WorkTop + Math.Max(0, (targetPrimary.WorkHeight - targetHeight) / 2);
            resizeNeeded = (targetWidth != curWidth || targetHeight != curHeight);
        }

        uint swpFlags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW;
        if (!resizeNeeded)
        {
            swpFlags |= Win32.SWP_NOSIZE;
        }

        if (wasMaximized)
        {
            // Restore, move into target monitor, then re-maximize
            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
            Win32.SetWindowPos(
                hWnd,
                IntPtr.Zero,
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                swpFlags);

            Win32.ShowWindow(hWnd, Win32.SW_MAXIMIZE);
            Win32.RedrawWindow(
                hWnd,
                IntPtr.Zero,
                IntPtr.Zero,
                Win32.RDW_INVALIDATE | Win32.RDW_UPDATENOW | Win32.RDW_ALLCHILDREN | Win32.RDW_FRAME);
        }
        else
        {
            Win32.SetWindowPos(
                hWnd,
                IntPtr.Zero,
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                swpFlags);

            Win32.RedrawWindow(
                hWnd,
                IntPtr.Zero,
                IntPtr.Zero,
                Win32.RDW_INVALIDATE | Win32.RDW_UPDATENOW | Win32.RDW_ALLCHILDREN | Win32.RDW_FRAME);
        }

        var logItem = new RelocationLogItem
        {
            Timestamp = DateTime.Now,
            ProcessName = processName,
            WindowTitle = windowTitle,
            SourceMonitor = sourceExcludedMonitor.DisplayTitle,
            TargetMonitor = targetPrimary.DisplayTitle
        };

        OnWindowRelocated?.Invoke(logItem);
        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int length = Win32.GetWindowTextLength(hWnd);
        if (length == 0) return "Untitled";
        var sb = new StringBuilder(length + 1);
        Win32.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var p = Process.GetProcessById((int)processId);
            return p.ProcessName;
        }
        catch
        {
            return "Application";
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
