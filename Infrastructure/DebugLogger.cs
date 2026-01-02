using System;
using System.IO;

namespace AudioProcessorAndStreamer.Infrastructure;

/// <summary>
/// Simple file-based logger for debugging issues outside of Visual Studio.
/// Logs to debug.log in LocalApplicationData folder.
/// </summary>
public static class DebugLogger
{
    private static string? _logPath;
    private static readonly object Lock = new();
    private static bool _initialized;

    public static string LogPath => _logPath ?? GetDefaultLogPath();

    private static string GetDefaultLogPath()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioProcessorAndStreamer");
        return Path.Combine(appDataDir, "debug.log");
    }

    /// <summary>
    /// Explicitly initialize the logger. Call this early in app startup.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioProcessorAndStreamer");
            Directory.CreateDirectory(appDataDir);
            _logPath = Path.Combine(appDataDir, "debug.log");

            // Clear log on startup
            File.WriteAllText(_logPath, $"=== AudioProcessorAndStreamer Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            File.AppendAllText(_logPath, $"Log file: {_logPath}\n\n");

            _initialized = true;
        }
        catch (Exception ex)
        {
            // Try writing to temp folder as fallback
            try
            {
                _logPath = Path.Combine(Path.GetTempPath(), "AudioProcessorAndStreamer_debug.log");
                File.WriteAllText(_logPath, $"=== AudioProcessorAndStreamer Debug Log (FALLBACK) - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
                File.AppendAllText(_logPath, $"Original error: {ex.Message}\n");
                File.AppendAllText(_logPath, $"Log file: {_logPath}\n\n");
                _initialized = true;
            }
            catch
            {
                // Can't write anywhere - give up silently
                _initialized = true;
            }
        }
    }

    public static void Log(string message)
    {
        // Auto-initialize if not done yet
        if (!_initialized) Initialize();

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}";

        // Also write to Debug output for VS
        System.Diagnostics.Debug.WriteLine(logLine);

        // Write to file
        if (_logPath != null)
        {
            lock (Lock)
            {
                try
                {
                    File.AppendAllText(_logPath, logLine + "\n");
                }
                catch
                {
                    // Ignore if we can't write
                }
            }
        }
    }

    public static void Log(string category, string message)
    {
        Log($"[{category}] {message}");
    }
}
