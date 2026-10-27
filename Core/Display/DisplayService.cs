using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using BarControl.Core.Native;

namespace BarControl.Core.Display;

public class DisplayService
{
    public record WmiMonitorData(string InstanceName, string FriendlyName, string SerialNumber);

    public static List<WmiMonitorData> GetWmiMonitors()
    {
        var list = new List<WmiMonitorData>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorID");
            foreach (ManagementObject mo in searcher.Get())
            {
                string instanceName = mo["InstanceName"]?.ToString() ?? string.Empty;

                string friendlyName = string.Empty;
                if (mo["UserFriendlyName"] is ushort[] nameChars)
                {
                    friendlyName = new string(nameChars.Where(c => c > 0).Select(c => (char)c).ToArray()).Trim();
                }

                string serial = string.Empty;
                if (mo["SerialNumberID"] is ushort[] serialChars)
                {
                    serial = new string(serialChars.Where(c => c > 0).Select(c => (char)c).ToArray()).Trim();
                }

                list.Add(new WmiMonitorData(instanceName, friendlyName, serial));
            }
        }
        catch (Exception)
        {
            // WMI may fail or be restricted on some environments; fallback gracefully
        }
        return list;
    }

    public static List<DisplayInfo> GetConnectedMonitors()
    {
        var wmiMonitors = GetWmiMonitors();
        var displays = new List<DisplayInfo>();
        int index = 1;

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref Win32.RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new Win32.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>();

            if (Win32.GetMonitorInfo(hMonitor, ref mi))
            {
                string devName = mi.szDevice;
                bool isPrimary = (mi.dwFlags & 1) != 0;

                string hardwareId = string.Empty;
                string deviceString = string.Empty;

                var dispDev = new Win32.DISPLAY_DEVICE();
                dispDev.cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>();

                if (Win32.EnumDisplayDevices(devName, 0, ref dispDev, 0))
                {
                    hardwareId = dispDev.DeviceID ?? string.Empty;
                    deviceString = dispDev.DeviceString ?? string.Empty;
                }

                // Match with WMI friendly name
                string friendlyName = string.Empty;
                if (!string.IsNullOrEmpty(hardwareId))
                {
                    // hardwareId looks like: MONITOR\DELA212\{...} or DISPLAY\MEI4024\...
                    var match = wmiMonitors.FirstOrDefault(w =>
                        (!string.IsNullOrEmpty(w.InstanceName) && hardwareId.Contains(w.InstanceName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(w.InstanceName) && w.InstanceName.Split('\\').Length > 1 && hardwareId.Contains(w.InstanceName.Split('\\')[1], StringComparison.OrdinalIgnoreCase)));

                    if (match != null && !string.IsNullOrWhiteSpace(match.FriendlyName))
                    {
                        friendlyName = match.FriendlyName;
                    }
                }

                // Secondary match if only count matches or substring match
                if (string.IsNullOrWhiteSpace(friendlyName) && wmiMonitors.Count > 0)
                {
                    var match = wmiMonitors.FirstOrDefault(w => !displays.Any(d => d.FriendlyName == w.FriendlyName));
                    if (match != null && !string.IsNullOrWhiteSpace(match.FriendlyName))
                    {
                        friendlyName = match.FriendlyName;
                    }
                }

                if (string.IsNullOrWhiteSpace(friendlyName))
                {
                    friendlyName = !string.IsNullOrWhiteSpace(deviceString) ? deviceString : $"Display {index}";
                }

                var display = new DisplayInfo
                {
                    Index = index++,
                    HMonitor = hMonitor,
                    DeviceName = devName,
                    FriendlyName = friendlyName,
                    HardwareId = hardwareId,
                    Left = mi.rcMonitor.Left,
                    Top = mi.rcMonitor.Top,
                    Right = mi.rcMonitor.Right,
                    Bottom = mi.rcMonitor.Bottom,
                    WorkLeft = mi.rcWork.Left,
                    WorkTop = mi.rcWork.Top,
                    WorkRight = mi.rcWork.Right,
                    WorkBottom = mi.rcWork.Bottom,
                    IsOsPrimary = isPrimary,
                    IsTargetPrimary = isPrimary, // Default target to OS primary
                    IsExcluded = false
                };

                displays.Add(display);
            }

            return true;
        }, IntPtr.Zero);

        // Associate displays with active audio endpoints (HDMI/DP soundbars and monitors)
        try
        {
            var audioEndpoints = Audio.Wasapi.GetActiveAudioEndpoints();
            var claimedAudioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var display in displays)
            {
                var match = audioEndpoints.FirstOrDefault(a =>
                    !claimedAudioIds.Contains(a.Id) &&
                    IsAudioEndpointMatch(display, a));

                if (match != null)
                {
                    claimedAudioIds.Add(match.Id);
                    display.HasAudioEndpoint = true;
                    display.AudioEndpointId = match.Id;
                    display.AudioEndpointName = match.FriendlyName;
                }
            }
        }
        catch
        {
            // Fallback gracefully if audio enumeration fails
        }

        return displays;
    }

    private static bool IsAudioEndpointMatch(DisplayInfo display, Audio.Wasapi.AudioEndpointInfo endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.FriendlyName))
            return false;

        // 1. Direct friendly name match:
        // e.g. "SOUNDSLAYER (NVIDIA High Definition Audio)" contains "SOUNDSLAYER"
        if (!string.IsNullOrWhiteSpace(display.FriendlyName) &&
            endpoint.FriendlyName.Contains(display.FriendlyName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Base name match (prefix before '('):
        // e.g. "AW3423DWF" from "AW3423DWF (NVIDIA High Definition Audio)"
        int parenIdx = endpoint.FriendlyName.IndexOf('(');
        string baseName = (parenIdx > 0 ? endpoint.FriendlyName.Substring(0, parenIdx) : endpoint.FriendlyName).Trim();
        if (baseName.Length >= 3 && !string.IsNullOrWhiteSpace(display.FriendlyName) &&
            display.FriendlyName.Contains(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Hardware ID model match:
        // e.g. "MEI4024" or "DELA212" from "MONITOR\MEI4024\..."
        if (!string.IsNullOrWhiteSpace(display.HardwareId))
        {
            var parts = display.HardwareId.Split('\\');
            if (parts.Length > 1 && parts[1].Length >= 4)
            {
                string model = parts[1].Trim();
                if (endpoint.FriendlyName.Contains(model, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
