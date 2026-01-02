namespace AudioProcessorAndStreamer.Models;

/// <summary>
/// Data structure for importing/exporting complete application configuration.
/// </summary>
public class ConfigExportData
{
    public AppConfiguration? AppConfig { get; set; }
    public List<StreamConfiguration>? Streams { get; set; }
}

public class AppConfiguration
{
    public string FfmpegPath { get; set; } = "FFmpeg/bin/ffmpeg.exe";
    public string HlsOutputDirectory { get; set; } = "stream_output";
    public int WebServerPort { get; set; } = 8080;
    public string BaseDomain { get; set; } = "http://localhost:8080";
    public List<StreamConfiguration> Streams { get; set; } = new();

    /// <summary>
    /// When enabled, audio processing and encoding only starts when there are active listeners.
    /// This saves CPU but causes a short delay when the first listener connects.
    /// </summary>
    public bool LazyProcessing { get; set; } = true;

    /// <summary>
    /// URL path for the HTML streams listing page (e.g., "/streams" or "/radio").
    /// </summary>
    public string StreamsPagePath { get; set; } = "/streams";

    /// <summary>
    /// When enabled, creates debug WAV files in each stream's HLS output folder for troubleshooting audio issues.
    /// WARNING: This generates large uncompressed audio files that grow continuously while streaming.
    /// </summary>
    public bool DebugAudioEnabled { get; set; } = false;

    /// <summary>
    /// Duration of each HLS segment in seconds. Lower values reduce latency but increase overhead.
    /// </summary>
    public int HlsSegmentDuration { get; set; } = 2;

    /// <summary>
    /// Number of segments to keep in the HLS playlist (history). Higher values allow more rewind capability.
    /// </summary>
    public int HlsPlaylistSize { get; set; } = 5;

    /// <summary>
    /// The audio output device name for monitor output. Leave empty to use the default device.
    /// Use the exact device name as shown in Windows audio settings.
    /// </summary>
    public string MonitorOutputDevice { get; set; } = "";

    /// <summary>
    /// Returns the base URL as configured (no auto port appending - allows for proxy setups).
    /// </summary>
    public string GetFullBaseUrl()
    {
        return BaseDomain.TrimEnd('/');
    }
}
