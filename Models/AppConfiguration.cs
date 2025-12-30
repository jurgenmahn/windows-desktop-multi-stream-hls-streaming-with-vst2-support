namespace AudioProcessorAndStreamer.Models;

public class AppConfiguration
{
    public string FfmpegPath { get; set; } = "FFmpeg/bin/ffmpeg.exe";
    public string HlsOutputDirectory { get; set; } = "hls_output";
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
    /// Returns the base URL as configured (no auto port appending - allows for proxy setups).
    /// </summary>
    public string GetFullBaseUrl()
    {
        return BaseDomain.TrimEnd('/');
    }
}
