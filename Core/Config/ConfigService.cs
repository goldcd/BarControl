using System;
using System.IO;
using System.Text.Json;

namespace BarControl.Core.Config;

public class ConfigService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarControl");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // Fallback to default on error
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception)
        {
            // Log or ignore file system write errors
        }
    }
}
