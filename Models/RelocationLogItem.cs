using System;

namespace BarControl.Models;

public class RelocationLogItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string SourceMonitor { get; set; } = string.Empty;
    public string TargetMonitor { get; set; } = string.Empty;
    public string TimeFormatted => Timestamp.ToString("HH:mm:ss");

    public string Summary => $"Moved [{ProcessName}] \"{Truncate(WindowTitle, 35)}\" from {SourceMonitor} → {TargetMonitor}";

    private static string Truncate(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return "Window";
        return value.Length <= maxLen ? value : value.Substring(0, maxLen - 3) + "...";
    }
}
