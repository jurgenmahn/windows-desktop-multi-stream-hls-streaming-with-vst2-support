namespace AudioProcessorAndStreamer.Models;

public class AppConfiguration
{
    public string FfmpegPath { get; set; } = "FFmpeg/ffmpeg.exe";
    public string HlsOutputDirectory { get; set; } = "hls_output";
    public int WebServerPort { get; set; } = 8080;
    public string BaseDomain { get; set; } = "http://localhost:8080";
    public List<StreamConfiguration> Streams { get; set; } = new();
}
