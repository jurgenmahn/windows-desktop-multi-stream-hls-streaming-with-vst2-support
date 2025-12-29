using System.IO;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Encoding;
using AudioProcessorAndStreamer.Services.Vst;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer.Services.Streaming;

public class StreamManager : IStreamManager
{
    private readonly Dictionary<string, AudioStreamProcessor> _activeStreams = new();
    private readonly IVstHostService _vstHost;
    private readonly IFfmpegService _ffmpegService;
    private readonly AppConfiguration _config;
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyDictionary<string, AudioStreamProcessor> ActiveStreams
    {
        get
        {
            lock (_lock)
            {
                return _activeStreams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
        }
    }

    public event EventHandler<StreamEventArgs>? StreamStarted;
    public event EventHandler<StreamEventArgs>? StreamStopped;
    public event EventHandler<StreamErrorEventArgs>? StreamError;

    public StreamManager(
        IVstHostService vstHost,
        IFfmpegService ffmpegService,
        IOptions<AppConfiguration> config)
    {
        _vstHost = vstHost;
        _ffmpegService = ffmpegService;
        _config = config.Value;

        // Ensure HLS output directory exists
        Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.HlsOutputDirectory));
    }

    public AudioStreamProcessor? StartStream(StreamConfiguration config)
    {
        lock (_lock)
        {
            if (_activeStreams.ContainsKey(config.Id))
            {
                return _activeStreams[config.Id];
            }
        }

        try
        {
            var hlsOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.HlsOutputDirectory);

            var processor = new AudioStreamProcessor(
                config,
                _vstHost,
                _ffmpegService,
                hlsOutputDir);

            processor.Stopped += (s, e) => OnStreamStopped(config.Id, config.Name);

            lock (_lock)
            {
                _activeStreams[config.Id] = processor;
            }

            processor.Start();
            StreamStarted?.Invoke(this, new StreamEventArgs(config.Id, config.Name));

            return processor;
        }
        catch (Exception ex)
        {
            StreamError?.Invoke(this, new StreamErrorEventArgs(
                config.Id,
                config.Name,
                $"Failed to start stream: {ex.Message}",
                ex));

            return null;
        }
    }

    private void OnStreamStopped(string streamId, string streamName)
    {
        lock (_lock)
        {
            if (_activeStreams.TryGetValue(streamId, out var processor))
            {
                _activeStreams.Remove(streamId);
                processor.Dispose();
            }
        }

        StreamStopped?.Invoke(this, new StreamEventArgs(streamId, streamName));
    }

    public void StopStream(string streamId)
    {
        AudioStreamProcessor? processor;

        lock (_lock)
        {
            if (!_activeStreams.TryGetValue(streamId, out processor))
            {
                return;
            }
        }

        processor.Stop();
    }

    public void StopAllStreams()
    {
        List<AudioStreamProcessor> processors;

        lock (_lock)
        {
            processors = _activeStreams.Values.ToList();
        }

        foreach (var processor in processors)
        {
            processor.Stop();
        }
    }

    public AudioStreamProcessor? GetStream(string streamId)
    {
        lock (_lock)
        {
            return _activeStreams.GetValueOrDefault(streamId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAllStreams();
    }
}
