using System.IO;
using AudioProcessorAndStreamer.Models;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer.Services.Encoding;

public class FfmpegService : IFfmpegService
{
    private readonly string _ffmpegPath;
    private readonly bool _isAvailable;
    private readonly int _hlsSegmentDuration;
    private readonly int _hlsPlaylistSize;

    public bool IsAvailable => _isAvailable;
    public string FfmpegPath => _ffmpegPath;

    public FfmpegService(IOptions<AppConfiguration> config)
    {
        _ffmpegPath = ResolveFfmpegPath(config.Value.FfmpegPath);
        _isAvailable = File.Exists(_ffmpegPath);
        _hlsSegmentDuration = config.Value.HlsSegmentDuration;
        _hlsPlaylistSize = config.Value.HlsPlaylistSize;

        if (_isAvailable)
        {
            // Configure FFMpegCore if we're using it elsewhere
            FFMpegCore.GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = Path.GetDirectoryName(_ffmpegPath) ?? "";
            });
        }
    }

    private static string ResolveFfmpegPath(string configuredPath)
    {
        // If absolute path, use as-is
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        // Check relative to application directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var relativePath = Path.Combine(appDir, configuredPath);

        if (File.Exists(relativePath))
        {
            return relativePath;
        }

        // Check in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathDirs = pathEnv.Split(Path.PathSeparator);

        foreach (var dir in pathDirs)
        {
            var fullPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        // Return the configured path as fallback
        return relativePath;
    }

    public FfmpegProcessManager CreateEncoder(
        EncodingProfile profile,
        string outputPath,
        int inputSampleRate,
        int inputChannels,
        bool debugAudioEnabled = false)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException($"FFmpeg not found at {_ffmpegPath}");
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        return new FfmpegProcessManager(
            _ffmpegPath,
            profile,
            outputPath,
            inputSampleRate,
            inputChannels,
            _hlsSegmentDuration,
            _hlsPlaylistSize,
            debugAudioEnabled);
    }
}
