using System;
using System.IO;

namespace AudioProcessorAndStreamer.Infrastructure;

/// <summary>
/// Simple file-based logger for debugging issues outside of Visual Studio.
/// Logs to debug.log in LocalApplicationData folder.
/// </summary>
public static class DebugLogger
{
    private static readonly string LogPath;
    private static readonly object Lock = new();

    static DebugLogger()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioProcessorAndStreamer");
        Directory.CreateDirectory(appDataDir);
        LogPath = Path.Combine(appDataDir, "debug.log");

        // Clear log on startup
        try
        {
            File.WriteAllText(LogPath, $"=== AudioProcessorAndStreamer Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n\n");
        }
        catch
        {
            // Ignore if we can't write
        }
    }

    public static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}";

        // Also write to Debug output for VS
        System.Diagnostics.Debug.WriteLine(logLine);

        // Write to file
        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogPath, logLine + "\n");
            }
            catch
            {
                // Ignore if we can't write
            }
        }
    }

    public static void Log(string category, string message)
    {
        Log($"[{category}] {message}");
    }
}
