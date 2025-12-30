using System.IO;
using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Audio;
using AudioProcessorAndStreamer.Services.Encoding;
using AudioProcessorAndStreamer.Services.Vst;

namespace AudioProcessorAndStreamer.Services.Streaming;

public class AudioStreamProcessor : IDisposable
{
    private readonly StreamConfiguration _config;
    private readonly IAudioCaptureService _capture;
    private readonly List<VstPluginInstance> _vstChain;
    private readonly List<FfmpegProcessManager> _encoders;
    private readonly CircularAudioBuffer _inputVisualizationBuffer;
    private readonly CircularAudioBuffer _outputVisualizationBuffer;
    private readonly AudioSmoothingBuffer? _outputSmoothingBuffer;
    private readonly int _blockSize;
    private float[] _processBuffer;
    private bool _isRunning;
    private bool _disposed;

    public string Id => _config.Id;
    public string Name => _config.Name;
    public bool IsRunning => _isRunning;

    public event EventHandler<float[]>? InputSamplesAvailable;
    public event EventHandler<float[]>? OutputSamplesAvailable;
    public event EventHandler<string>? EncoderMessage;
    public event EventHandler? Stopped;

    public AudioStreamProcessor(
        StreamConfiguration config,
        IVstHostService vstHost,
        IFfmpegService ffmpegService,
        string hlsOutputDirectory,
        double vstOutputBufferSeconds = 0)
    {
        _config = config;
        _blockSize = config.AudioInput.BufferSize;

        // Create audio capture based on driver type
        _capture = config.AudioInput.DriverType switch
        {
            AudioDriverType.Asio => new AsioCaptureService(config.AudioInput),
            _ => new WasapiCaptureService(config.AudioInput)
        };

        // Create visualization buffers (store ~1 second of audio)
        int bufferSize = config.AudioInput.SampleRate * config.AudioInput.Channels;
        _inputVisualizationBuffer = new CircularAudioBuffer(bufferSize);
        _outputVisualizationBuffer = new CircularAudioBuffer(bufferSize);
        _processBuffer = new float[_blockSize * config.AudioInput.Channels * 2];

        // Create output smoothing buffer if configured
        if (vstOutputBufferSeconds > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[{config.Name}] Creating smoothing buffer: {vstOutputBufferSeconds}s buffer, 20ms output chunks");
            _outputSmoothingBuffer = new AudioSmoothingBuffer(
                vstOutputBufferSeconds,
                outputChunkMs: 20, // Output 20ms chunks for smooth 50fps display
                config.AudioInput.SampleRate,
                config.AudioInput.Channels);
            _outputSmoothingBuffer.ChunkReady += OnSmoothedOutputReady;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[{config.Name}] No smoothing buffer (vstOutputBufferSeconds={vstOutputBufferSeconds})");
        }

        // Load VST plugins in order
        _vstChain = new List<VstPluginInstance>();
        foreach (var vstConfig in config.VstPlugins.OrderBy(p => p.Order))
        {
            var plugin = vstHost.LoadPlugin(vstConfig);
            if (plugin != null)
            {
                plugin.Initialize(config.AudioInput.SampleRate, _blockSize);
                plugin.IsBypassed = vstConfig.IsBypassed;

                // Load preset file if configured
                if (!string.IsNullOrEmpty(vstConfig.PresetFilePath))
                {
                    if (plugin.LoadPresetFromFile(vstConfig.PresetFilePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Loaded preset for {plugin.PluginName}: {vstConfig.PresetFilePath}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load preset for {plugin.PluginName}: {vstConfig.PresetFilePath}");
                    }
                }

                _vstChain.Add(plugin);
            }
        }

        // Create encoders for each profile
        _encoders = new List<FfmpegProcessManager>();
        var streamOutputDir = Path.Combine(hlsOutputDirectory, config.StreamPath ?? config.Id);

        // Ensure output directory exists
        Directory.CreateDirectory(streamOutputDir);

        foreach (var profile in config.EncodingProfiles)
        {
            var outputFile = Path.Combine(streamOutputDir, $"{profile.Name.ToLowerInvariant().Replace(" ", "_")}.m3u8");

            try
            {
                var encoder = ffmpegService.CreateEncoder(
                    profile,
                    outputFile,
                    config.AudioInput.SampleRate,
                    config.AudioInput.Channels);

                encoder.ErrorDataReceived += (s, msg) => EncoderMessage?.Invoke(this, msg);
                encoder.ProcessExited += OnEncoderExited;

                _encoders.Add(encoder);
            }
            catch (Exception ex)
            {
                EncoderMessage?.Invoke(this, $"Failed to create encoder for {profile.Name}: {ex.Message}");
            }
        }

        // Create master playlist referencing all encoding profiles
        CreateMasterPlaylist(streamOutputDir, config.EncodingProfiles);

        // Wire up audio processing
        _capture.DataAvailable += OnAudioDataAvailable;
    }

    private void CreateMasterPlaylist(string outputDir, List<EncodingProfile> profiles)
    {
        var masterPath = Path.Combine(outputDir, "master.m3u8");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");

        foreach (var profile in profiles)
        {
            var playlistName = $"{profile.Name.ToLowerInvariant().Replace(" ", "_")}.m3u8";
            var bandwidth = profile.Bitrate;

            // Audio codec based on profile codec
            var codec = profile.Codec switch
            {
                AudioCodec.Aac => "mp4a.40.2",
                AudioCodec.Mp3 => "mp4a.40.34",
                AudioCodec.Opus => "opus",
                _ => "mp4a.40.2"
            };

            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},CODECS=\"{codec}\"");
            sb.AppendLine(playlistName);
        }

        try
        {
            File.WriteAllText(masterPath, sb.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create master playlist: {ex.Message}");
        }
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        var inputSamples = e.Buffer;

        // Update input visualization (copy to avoid reference issues)
        var inputCopy = new float[inputSamples.Length];
        Array.Copy(inputSamples, inputCopy, inputSamples.Length);
        _inputVisualizationBuffer.Write(inputCopy);
        InputSamplesAvailable?.Invoke(this, inputCopy);

        // Process through VST chain
        var processedSamples = inputSamples;

        if (_vstChain.Count > 0)
        {
            // Ensure process buffer is large enough
            if (_processBuffer.Length < inputSamples.Length)
            {
                _processBuffer = new float[inputSamples.Length];
            }

            foreach (var vst in _vstChain)
            {
                vst.ProcessAudio(processedSamples, _processBuffer, e.Channels);

                // Swap buffers for next iteration
                if (_vstChain.IndexOf(vst) < _vstChain.Count - 1)
                {
                    (processedSamples, _processBuffer) = (_processBuffer, processedSamples);
                }
                else
                {
                    processedSamples = _processBuffer;
                }
            }
        }

        // Route output through smoothing buffer if available, otherwise direct
        if (_outputSmoothingBuffer != null)
        {
            // Copy and send to smoothing buffer - it will fire ChunkReady events
            var outputCopy = new float[processedSamples.Length];
            Array.Copy(processedSamples, outputCopy, processedSamples.Length);
            _outputSmoothingBuffer.Write(outputCopy);
        }
        else
        {
            // Direct output without smoothing
            SendOutputToConsumers(processedSamples);
        }
    }

    private void OnSmoothedOutputReady(object? sender, float[] samples)
    {
        if (!_isRunning) return;
        SendOutputToConsumers(samples);
    }

    private void SendOutputToConsumers(float[] samples)
    {
        if (!_isRunning) return;

        // Update output visualization
        _outputVisualizationBuffer.Write(samples);
        OutputSamplesAvailable?.Invoke(this, samples);

        // Convert to PCM16 and send to encoders
        var pcmBytes = AudioSampleConverter.FloatToPcm16(samples);

        foreach (var encoder in _encoders)
        {
            encoder.WriteAudioData(pcmBytes);
        }
    }

    private void OnEncoderExited(object? sender, EventArgs e)
    {
        // Check if all encoders have stopped
        if (_encoders.All(enc => !enc.IsRunning))
        {
            Stop();
        }
    }

    public void SetVstBypassed(bool bypassed)
    {
        foreach (var vst in _vstChain)
        {
            vst.IsBypassed = bypassed;
        }
    }

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _capture.StartCapture();
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _capture.StopCapture();

        foreach (var encoder in _encoders)
        {
            encoder.Stop();
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public float[] GetLatestInputSamples(int count)
    {
        return _inputVisualizationBuffer.ReadLatest(count);
    }

    public float[] GetLatestOutputSamples(int count)
    {
        return _outputVisualizationBuffer.ReadLatest(count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _capture.DataAvailable -= OnAudioDataAvailable;
        _capture.Dispose();

        if (_outputSmoothingBuffer != null)
        {
            _outputSmoothingBuffer.ChunkReady -= OnSmoothedOutputReady;
            _outputSmoothingBuffer.Clear();
        }

        foreach (var vst in _vstChain)
        {
            vst.Dispose();
        }
        _vstChain.Clear();

        foreach (var encoder in _encoders)
        {
            encoder.Dispose();
        }
        _encoders.Clear();
    }
}
