using System.IO;
using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Encoding;
using AudioProcessorAndStreamer.Services.Vst;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer.Services.Streaming;

public class StreamManager : IStreamManager
{
    private readonly Dictionary<string, AudioStreamProcessor> _activeStreams = new();
    private readonly Dictionary<string, CancellationTokenSource> _stopEncodingTimers = new();
    private readonly IVstHostService _vstHost;
    private readonly IFfmpegService _ffmpegService;
    private readonly AppConfiguration _config;
    private readonly object _lock = new();
    private readonly TimeSpan _noListenerTimeout = TimeSpan.FromSeconds(30);
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
        Directory.CreateDirectory(ResolveHlsOutputDirectory(_config.HlsOutputDirectory));
    }

    private static string ResolveHlsOutputDirectory(string hlsOutputDirectory)
    {
        // If absolute path, use as-is
        if (Path.IsPathRooted(hlsOutputDirectory))
        {
            return hlsOutputDirectory;
        }

        // For relative paths, use LocalApplicationData (writable location)
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioProcessorAndStreamer");
        return Path.Combine(appDataDir, hlsOutputDirectory);
    }

    public AudioStreamProcessor? StartStream(StreamConfiguration config)
    {
        DebugLogger.Log("StreamManager", $"StartStream() called for '{config.Name}' (ID: {config.Id})");

        lock (_lock)
        {
            if (_activeStreams.ContainsKey(config.Id))
            {
                DebugLogger.Log("StreamManager", $"'{config.Name}' already in active streams - returning existing processor");
                return _activeStreams[config.Id];
            }
        }

        DebugLogger.Log("StreamManager", $"'{config.Name}' - creating new AudioStreamProcessor");

        try
        {
            var hlsOutputDir = ResolveHlsOutputDirectory(_config.HlsOutputDirectory);
            DebugLogger.Log("StreamManager", $"'{config.Name}' - HLS output dir: {hlsOutputDir}");

            DebugLogger.Log("StreamManager", $"'{config.Name}' - creating AudioStreamProcessor instance...");
            var processor = new AudioStreamProcessor(
                config,
                _vstHost,
                _ffmpegService,
                hlsOutputDir,
                _config.DebugAudioEnabled,
                _config.LazyProcessing,
                _config.HlsSegmentDuration,
                _config.HlsPlaylistSize);
            DebugLogger.Log("StreamManager", $"'{config.Name}' - AudioStreamProcessor created");

            processor.Stopped += (s, e) => OnStreamStopped(config.Id, config.Name);

            // Check for initialization errors before starting
            if (processor.HasInitializationErrors)
            {
                DebugLogger.Log("StreamManager", $"'{config.Name}' - Initialization errors detected, not starting stream");
                // Return the processor so caller can check errors, but don't add to active streams
                return processor;
            }

            lock (_lock)
            {
                _activeStreams[config.Id] = processor;
            }

            try
            {
                DebugLogger.Log("StreamManager", $"'{config.Name}' - calling processor.Start()...");
                processor.Start();
                DebugLogger.Log("StreamManager", $"'{config.Name}' - processor.Start() completed");
            }
            catch (Exception ex)
            {
                DebugLogger.Log("StreamManager", $"'{config.Name}' - ERROR: processor.Start() failed: {ex.Message}\n{ex.StackTrace}");
                lock (_lock)
                {
                    _activeStreams.Remove(config.Id);
                }
                processor.Dispose();
                StreamError?.Invoke(this, new StreamErrorEventArgs(
                    config.Id,
                    config.Name,
                    $"Failed to start stream: {ex.Message}",
                    ex));
                return null;
            }

            // Warn user about sample rate mismatch with WASAPI
            if (processor.HasSampleRateMismatch)
            {
                var warning = $"WARNING: Sample rate mismatch! Configured {processor.ConfiguredSampleRate}Hz but device is {processor.ActualSampleRate}Hz. Using device rate.";
                DebugLogger.Log("StreamManager", $"'{config.Name}' - {warning}");
                // Fire through StreamError as a warning (not a fatal error)
                StreamError?.Invoke(this, new StreamErrorEventArgs(
                    config.Id,
                    config.Name,
                    warning,
                    null));
            }

            DebugLogger.Log("StreamManager", $"'{config.Name}' - Stream started successfully");
            StreamStarted?.Invoke(this, new StreamEventArgs(config.Id, config.Name));

            return processor;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("StreamManager", $"'{config.Name}' - EXCEPTION in StartStream: {ex.Message}\n{ex.StackTrace}");
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

    /// <summary>
    /// Gets a stream processor by its stream path (not ID).
    /// </summary>
    private AudioStreamProcessor? GetStreamByPath(string streamPath)
    {
        lock (_lock)
        {
            return _activeStreams.Values.FirstOrDefault(p => p.StreamPath == streamPath);
        }
    }

    public void OnListenerConnected(string streamPath)
    {
        if (!_config.LazyProcessing) return;

        var processor = GetStreamByPath(streamPath);
        if (processor == null || !processor.IsRunning) return;

        // Cancel any pending stop timer for this stream
        lock (_lock)
        {
            if (_stopEncodingTimers.TryGetValue(streamPath, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _stopEncodingTimers.Remove(streamPath);
                System.Diagnostics.Debug.WriteLine($"[{streamPath}] Cancelled encoding stop timer - listener connected");
            }
        }

        // Start encoding if not already active
        if (!processor.IsEncodingActive)
        {
            processor.StartEncoding();
            System.Diagnostics.Debug.WriteLine($"[{streamPath}] Started encoding - first listener connected");
        }
    }

    public void OnNoListeners(string streamPath)
    {
        if (!_config.LazyProcessing) return;

        var processor = GetStreamByPath(streamPath);
        if (processor == null || !processor.IsRunning || !processor.IsEncodingActive) return;

        // Schedule encoding stop after timeout
        lock (_lock)
        {
            // Cancel any existing timer
            if (_stopEncodingTimers.TryGetValue(streamPath, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _stopEncodingTimers[streamPath] = cts;

            System.Diagnostics.Debug.WriteLine($"[{streamPath}] Scheduling encoding stop in {_noListenerTimeout.TotalSeconds}s - no listeners");

            // Start timer to stop encoding
            Task.Delay(_noListenerTimeout, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;

                var proc = GetStreamByPath(streamPath);
                if (proc != null && proc.IsRunning && proc.IsEncodingActive)
                {
                    proc.StopEncoding();
                    System.Diagnostics.Debug.WriteLine($"[{streamPath}] Stopped encoding - no listeners for {_noListenerTimeout.TotalSeconds}s");
                }

                lock (_lock)
                {
                    _stopEncodingTimers.Remove(streamPath);
                }
            }, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel all pending stop timers
        lock (_lock)
        {
            foreach (var cts in _stopEncodingTimers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _stopEncodingTimers.Clear();
        }

        StopAllStreams();
    }
}
