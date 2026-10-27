using System;
using System.IO;
using System.Text;

namespace BarControl.Core.Logging;

public static class LogService
{
    private static readonly object _lock = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarControl");

    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "barcontrol.log");

    static LogService()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }
        catch { }
    }

    public static void LogInfo(string message) => Write("INFO", message);
    public static void LogWarn(string message) => Write("WARN", message);
    public static void LogDebug(string message) => Write("DEBUG", message);

    public static void LogError(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
        }
        Write("ERROR", sb.ToString());
    }

    private static readonly string? WorkspaceLogFilePath = Directory.Exists(@"e:\projects\BarControl")
        ? @"e:\projects\BarControl\barcontrol.log"
        : null;

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                if (WorkspaceLogFilePath != null)
                {
                    File.AppendAllText(WorkspaceLogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch
            {
                // Never throw from logger
            }
        }
    }
}
