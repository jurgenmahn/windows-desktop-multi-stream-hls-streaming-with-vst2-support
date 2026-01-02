using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioProcessorAndStreamer.Infrastructure;

namespace AudioProcessorAndStreamer.Services;

public class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }
}

public class AutoUpdateService
{
    private const string UpdateCheckUrl = "https://www.mahn.it/software/audioprocessorandstreamer/autoupdate.json";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Version CurrentVersion { get; }

    public AutoUpdateService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        CurrentVersion = assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// Checks for updates silently. Returns update info if available, null otherwise.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            DebugLogger.Log("AutoUpdate", $"Checking for updates at {UpdateCheckUrl}");

            var response = await HttpClient.GetStringAsync(UpdateCheckUrl);
            var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(response);

            if (updateInfo == null || string.IsNullOrEmpty(updateInfo.Version))
            {
                DebugLogger.Log("AutoUpdate", "Invalid update info received");
                return null;
            }

            DebugLogger.Log("AutoUpdate", $"Remote version: {updateInfo.Version}, Current version: {CurrentVersion}");

            if (!Version.TryParse(updateInfo.Version, out var remoteVersion))
            {
                DebugLogger.Log("AutoUpdate", $"Failed to parse remote version: {updateInfo.Version}");
                return null;
            }

            // Compare versions - only return if remote is newer
            if (remoteVersion > CurrentVersion)
            {
                DebugLogger.Log("AutoUpdate", $"Update available: {updateInfo.Version}");
                return updateInfo;
            }

            DebugLogger.Log("AutoUpdate", "No update available (current version is up to date)");
            return null;
        }
        catch (HttpRequestException ex)
        {
            DebugLogger.Log("AutoUpdate", $"Network error checking for updates: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            DebugLogger.Log("AutoUpdate", "Update check timed out");
            return null;
        }
        catch (JsonException ex)
        {
            DebugLogger.Log("AutoUpdate", $"Failed to parse update JSON: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("AutoUpdate", $"Unexpected error checking for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the update installer to the temp folder.
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null)
    {
        try
        {
            DebugLogger.Log("AutoUpdate", $"Downloading update from {updateInfo.DownloadUrl}");

            var tempPath = Path.Combine(Path.GetTempPath(), $"AudioProcessorAndStreamer-Setup-{updateInfo.Version}.exe");

            // Delete existing file if present
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using var response = await HttpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report((double)downloadedBytes / totalBytes * 100);
                }
            }

            DebugLogger.Log("AutoUpdate", $"Download complete: {tempPath}");
            return tempPath;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("AutoUpdate", $"Failed to download update: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Launches the installer and optionally closes the application.
    /// </summary>
    public bool LaunchInstaller(string installerPath)
    {
        try
        {
            DebugLogger.Log("AutoUpdate", $"Launching installer: {installerPath}");

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("AutoUpdate", $"Failed to launch installer: {ex.Message}");
            return false;
        }
    }
}
