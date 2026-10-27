using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace BarControl.Core.Update;

public record UpdateInfo(string Version, string DownloadUrl, string ReleaseNotes, string HtmlUrl);

public class UpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/goldcd/BarControl/releases/latest";
    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BarControl-Updater", "1.0"));
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Checks GitHub for the latest release and compares with the currently running version.
    /// Returns UpdateInfo if a newer release is found, or null otherwise.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp))
                return null;

            string tagName = tagProp.GetString() ?? string.Empty;
            string cleanTag = tagName.TrimStart('v', 'V', '.');

            if (!Version.TryParse(cleanTag, out var remoteVersion))
                return null;

            var currentVersion = typeof(UpdateService).Assembly.GetName().Version;
            if (currentVersion == null)
                return null;

            // Normalize both versions to 4 components (Major.Minor.Build.Revision) so e.g. "v0.1" == "0.1.0.0"
            var normalizedRemote = new Version(
                remoteVersion.Major,
                remoteVersion.Minor,
                Math.Max(remoteVersion.Build, 0),
                Math.Max(remoteVersion.Revision, 0));

            var normalizedCurrent = new Version(
                currentVersion.Major,
                currentVersion.Minor,
                Math.Max(currentVersion.Build, 0),
                Math.Max(currentVersion.Revision, 0));

            // Only notify if remote version is strictly newer
            if (normalizedRemote <= normalizedCurrent)
                return null;

            string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? string.Empty : string.Empty;
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

            // Find BarControl.exe in the release assets
            string downloadUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp) &&
                        nameProp.GetString()?.Equals("BarControl.exe", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (asset.TryGetProperty("browser_download_url", out var dlProp))
                        {
                            downloadUrl = dlProp.GetString() ?? string.Empty;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                return null;

            return new UpdateInfo($"v{cleanTag}", downloadUrl, body, htmlUrl);
        }
        catch
        {
            // Network failure, rate limit, or offline: fail silently without disrupting the user
            return null;
        }
    }

    /// <summary>
    /// Downloads the updated executable and spawns a background swap script to replace
    /// the current executable after BarControl terminates.
    /// </summary>
    public async Task ApplyUpdateAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("Could not determine current executable path.");

        string tempFile = Path.Combine(Path.GetTempPath(), $"BarControl_update_{Guid.NewGuid():N}.exe");

        // Download new executable
        using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long bytesReadTotal = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                bytesReadTotal += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)bytesReadTotal / totalBytes);
                }
            }
        }

        // Integrity check: standalone single-file .NET 8 app should be at least 5 MB
        var fileInfo = new FileInfo(tempFile);
        if (fileInfo.Length < 5 * 1024 * 1024)
        {
            try { File.Delete(tempFile); } catch { }
            throw new InvalidOperationException("Downloaded update file appears corrupted or incomplete.");
        }

        // Generate self-cleaning batch script to swap the binary once BarControl exits
        string scriptPath = Path.Combine(Path.GetTempPath(), $"barcontrol_update_{Guid.NewGuid():N}.bat");
        string scriptContent = $@"@echo off
timeout /t 1 /nobreak >nul
:retry
move /y ""{tempFile}"" ""{currentExe}"" >nul 2>&1
if exist ""{tempFile}"" (
    timeout /t 1 /nobreak >nul
    goto retry
)
start """" ""{currentExe}""
del ""%~f0""
";
        await File.WriteAllTextAsync(scriptPath, scriptContent);

        // Spawn script invisibly and exit BarControl
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current is App app)
            {
                app.ShutdownApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
        });
    }
}
