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
    private readonly IFfmpegService _ffmpegService;
    private readonly List<VstPluginInstance> _vstChain;
    private readonly List<FfmpegProcessManager> _encoders;
    private readonly CircularAudioBuffer _inputVisualizationBuffer;
    private readonly CircularAudioBuffer _outputVisualizationBuffer;
    private readonly int _blockSize;
    private readonly int _actualSampleRate;
    private readonly int _actualChannels;
    private readonly string _streamOutputDir;
    private readonly bool _debugAudioEnabled;
    private readonly bool _lazyProcessing;
    private FileStream? _debugBeforeVstStream;
    private long _debugBeforeVstBytes;
    private float[] _processBuffer;
    private float[] _vstInputBuffer;      // Fixed-size buffer for VST input
    private float[] _vstOutputBuffer;     // Fixed-size buffer for VST output
    private float[] _accumulatorBuffer;   // Ring buffer for accumulating samples
    private int _accumulatorWritePos;     // Write position in accumulator
    private int _accumulatorReadPos;      // Read position in accumulator
    private int _accumulatorCount;        // Number of samples in accumulator
    private readonly int _vstBlockSamples; // Block size in total samples (blockSize * channels)
    private bool _isRunning;
    private bool _isEncodingActive;
    private bool _disposed;

    public string Id => _config.Id;
    public string StreamPath => _config.StreamPath ?? _config.Id;
    public string Name => _config.Name;
    public bool IsRunning => _isRunning;
    public bool IsEncodingActive => _isEncodingActive;
    public int ActualSampleRate => _actualSampleRate;
    public int ConfiguredSampleRate => _config.AudioInput.SampleRate;
    public bool HasSampleRateMismatch => _config.AudioInput.DriverType == AudioDriverType.Wasapi
                                         && _config.AudioInput.SampleRate != _actualSampleRate;

    public event EventHandler<float[]>? InputSamplesAvailable;
    public event EventHandler<float[]>? OutputSamplesAvailable;
    public event EventHandler<string>? EncoderMessage;
    public event EventHandler? Stopped;
    public event EventHandler<bool>? EncodingStateChanged;

    public AudioStreamProcessor(
        StreamConfiguration config,
        IVstHostService vstHost,
        IFfmpegService ffmpegService,
        string hlsOutputDirectory,
        bool debugAudioEnabled = false,
        bool lazyProcessing = false)
    {
        _config = config;
        _blockSize = config.AudioInput.BufferSize;
        _debugAudioEnabled = debugAudioEnabled;
        _lazyProcessing = lazyProcessing;
        _ffmpegService = ffmpegService;

        // Create audio capture based on driver type
        _capture = config.AudioInput.DriverType switch
        {
            AudioDriverType.Asio => new AsioCaptureService(config.AudioInput),
            _ => new WasapiCaptureService(config.AudioInput)
        };

        // Use the ACTUAL sample rate from the capture device, not the configured one
        // This is critical - WASAPI captures at the device's native rate
        _actualSampleRate = _capture.WaveFormat.SampleRate;
        _actualChannels = _capture.WaveFormat.Channels;

        System.Diagnostics.Debug.WriteLine($"[{config.Name}] Configured: {config.AudioInput.SampleRate}Hz, Actual device: {_actualSampleRate}Hz, {_actualChannels}ch");

        // Check for sample rate mismatch with WASAPI
        if (config.AudioInput.DriverType == AudioDriverType.Wasapi && config.AudioInput.SampleRate != _actualSampleRate)
        {
            System.Diagnostics.Debug.WriteLine($"[{config.Name}] WARNING: Sample rate mismatch! Using device rate {_actualSampleRate}Hz");
        }

        // Create visualization buffers (store ~1 second of audio)
        int bufferSize = _actualSampleRate * _actualChannels;
        _inputVisualizationBuffer = new CircularAudioBuffer(bufferSize);
        _outputVisualizationBuffer = new CircularAudioBuffer(bufferSize);
        _processBuffer = new float[_blockSize * _actualChannels * 2];

        // Initialize VST block buffering - process in fixed-size blocks for stable VST timing
        _vstBlockSamples = _blockSize * _actualChannels;
        _vstInputBuffer = new float[_vstBlockSamples];
        _vstOutputBuffer = new float[_vstBlockSamples];
        // Accumulator holds 4x block size to handle timing variations
        _accumulatorBuffer = new float[_vstBlockSamples * 4];
        _accumulatorWritePos = 0;
        _accumulatorReadPos = 0;
        _accumulatorCount = 0;

        // Load VST plugins in order
        _vstChain = new List<VstPluginInstance>();
        foreach (var vstConfig in config.VstPlugins.OrderBy(p => p.Order))
        {
            var plugin = vstHost.LoadPlugin(vstConfig);
            if (plugin != null)
            {
                plugin.Initialize(_actualSampleRate, _blockSize);
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

        // Initialize encoder list and output directory
        _encoders = new List<FfmpegProcessManager>();
        _streamOutputDir = Path.Combine(hlsOutputDirectory, config.StreamPath ?? config.Id);

        // Ensure output directory exists
        Directory.CreateDirectory(_streamOutputDir);

        // Create master playlist referencing all encoding profiles (needed even for lazy processing)
        CreateMasterPlaylist(_streamOutputDir, config.EncodingProfiles);

        // If not using lazy processing, create encoders immediately
        if (!_lazyProcessing)
        {
            CreateEncoders();
            _isEncodingActive = true;
        }

        // Wire up audio processing
        _capture.DataAvailable += OnAudioDataAvailable;
    }

    private void CreateMasterPlaylist(string outputDir, List<EncodingProfile> profiles)
    {
        // For DASH, FFmpeg generates the MPD manifest automatically
        // We only need to create master playlist for HLS
        if (_config.StreamFormat == StreamFormat.Dash)
        {
            // DASH doesn't need a master playlist - FFmpeg creates the MPD
            // However, we should create a simple redirect/info file
            System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Using DASH format - MPD will be generated by FFmpeg");
            return;
        }

        var masterPath = Path.Combine(outputDir, "master.m3u8");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#EXTM3U");

        // Use version 7 for fMP4 support, version 3 for MPEG-TS
        var hlsVersion = _config.ContainerFormat == ContainerFormat.Fmp4 ? 7 : 3;
        sb.AppendLine($"#EXT-X-VERSION:{hlsVersion}");

        if (_config.ContainerFormat == ContainerFormat.Fmp4)
        {
            sb.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");
        }

        // Order by bitrate descending so highest quality is first (players often pick first)
        foreach (var profile in profiles.OrderByDescending(p => p.Bitrate))
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

    private void CreateEncoders()
    {
        if (_encoders.Count > 0) return; // Already created

        // Clean up any leftover files from previous runs before starting
        CleanupStreamOutputFolder();

        // Create debug WAV file for raw capture (before VST)
        if (_debugAudioEnabled)
        {
            var debugPath = Path.Combine(_streamOutputDir, "debug_before_vst.wav");
            try
            {
                if (File.Exists(debugPath))
                {
                    File.Delete(debugPath);
                }
                _debugBeforeVstStream = new FileStream(debugPath, FileMode.Create, FileAccess.Write);
                WriteWavHeader(_debugBeforeVstStream, _actualSampleRate, _actualChannels, 0);
                System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Debug before-VST file: {debugPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Failed to create debug file: {ex.Message}");
            }
        }

        foreach (var profile in _config.EncodingProfiles)
        {
            // Output file extension depends on stream format
            var extension = _config.StreamFormat == StreamFormat.Dash ? ".mpd" : ".m3u8";
            var outputFile = Path.Combine(_streamOutputDir, $"{profile.Name.ToLowerInvariant().Replace(" ", "_")}{extension}");

            try
            {
                var encoder = _ffmpegService.CreateEncoder(
                    profile,
                    outputFile,
                    _actualSampleRate,
                    _actualChannels,
                    _config.StreamFormat,
                    _config.ContainerFormat,
                    _debugAudioEnabled);

                encoder.ErrorDataReceived += (s, msg) => EncoderMessage?.Invoke(this, msg);
                encoder.ProcessExited += OnEncoderExited;

                _encoders.Add(encoder);
            }
            catch (Exception ex)
            {
                EncoderMessage?.Invoke(this, $"Failed to create encoder for {profile.Name}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Created {_encoders.Count} encoders");
    }

    private void DestroyEncoders()
    {
        // Unsubscribe from events before stopping to avoid callbacks on disposed objects
        foreach (var encoder in _encoders)
        {
            encoder.ProcessExited -= OnEncoderExited;
        }

        foreach (var encoder in _encoders)
        {
            encoder.Stop();
            encoder.Dispose();
        }
        _encoders.Clear();

        // Finalize debug file
        FinalizeDebugWavFile();

        System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Destroyed encoders");
    }

    /// <summary>
    /// Starts encoding (creates FFmpeg processes if using lazy processing).
    /// Called when first listener connects.
    /// </summary>
    public void StartEncoding()
    {
        if (_isEncodingActive || !_isRunning) return;

        CreateEncoders();
        _isEncodingActive = true;

        System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Encoding started (lazy)");
        EncodingStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Stops encoding (destroys FFmpeg processes if using lazy processing).
    /// Called when no listeners remain.
    /// </summary>
    public void StopEncoding()
    {
        if (!_isEncodingActive) return;

        _isEncodingActive = false;

        if (_lazyProcessing)
        {
            // Only destroy encoders if using lazy processing
            DestroyEncoders();
        }

        System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Encoding stopped (lazy)");
        EncodingStateChanged?.Invoke(this, false);
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        var inputSamples = e.Buffer;

        // Write raw capture to debug file (before VST)
        if (_debugBeforeVstStream != null)
        {
            var pcmBytes = AudioSampleConverter.FloatToPcm16(inputSamples);
            try
            {
                _debugBeforeVstStream.Write(pcmBytes, 0, pcmBytes.Length);
                _debugBeforeVstBytes += pcmBytes.Length;
            }
            catch { }
        }

        // Update input visualization (copy to avoid reference issues)
        var inputCopy = new float[inputSamples.Length];
        Array.Copy(inputSamples, inputCopy, inputSamples.Length);
        _inputVisualizationBuffer.Write(inputCopy);
        InputSamplesAvailable?.Invoke(this, inputCopy);

        // If no VST plugins, pass through directly
        if (_vstChain.Count == 0)
        {
            SendOutputToConsumers(inputSamples);
            return;
        }

        // Add samples to accumulator buffer for block-based VST processing
        AddToAccumulator(inputSamples);

        // Process complete blocks through VST
        while (_accumulatorCount >= _vstBlockSamples)
        {
            // Read a complete block from accumulator
            ReadFromAccumulator(_vstInputBuffer, _vstBlockSamples);

            // Process through VST chain with fixed block size
            var processedSamples = _vstInputBuffer;
            var outputBuffer = _vstOutputBuffer;

            foreach (var vst in _vstChain)
            {
                vst.ProcessAudio(processedSamples, outputBuffer, _actualChannels);

                // Swap buffers for next iteration
                if (_vstChain.IndexOf(vst) < _vstChain.Count - 1)
                {
                    (processedSamples, outputBuffer) = (outputBuffer, processedSamples);
                }
                else
                {
                    processedSamples = outputBuffer;
                }
            }

            // Send processed block to consumers
            SendOutputToConsumers(processedSamples);
        }
    }

    private void AddToAccumulator(float[] samples)
    {
        int samplesToWrite = samples.Length;
        int bufferLength = _accumulatorBuffer.Length;

        for (int i = 0; i < samplesToWrite; i++)
        {
            _accumulatorBuffer[_accumulatorWritePos] = samples[i];
            _accumulatorWritePos = (_accumulatorWritePos + 1) % bufferLength;
        }
        _accumulatorCount += samplesToWrite;

        // Prevent overflow - if we're falling behind, drop oldest samples
        if (_accumulatorCount > bufferLength)
        {
            int overflow = _accumulatorCount - bufferLength;
            _accumulatorReadPos = (_accumulatorReadPos + overflow) % bufferLength;
            _accumulatorCount = bufferLength;
            System.Diagnostics.Debug.WriteLine($"[{_config.Name}] VST buffer overflow, dropped {overflow} samples");
        }
    }

    private void ReadFromAccumulator(float[] output, int count)
    {
        int bufferLength = _accumulatorBuffer.Length;

        for (int i = 0; i < count; i++)
        {
            output[i] = _accumulatorBuffer[_accumulatorReadPos];
            _accumulatorReadPos = (_accumulatorReadPos + 1) % bufferLength;
        }
        _accumulatorCount -= count;
    }

    private void SendOutputToConsumers(float[] samples)
    {
        if (!_isRunning) return;

        // Update output visualization
        _outputVisualizationBuffer.Write(samples);
        OutputSamplesAvailable?.Invoke(this, samples);

        // Only send to encoders if encoding is active
        if (!_isEncodingActive) return;

        // Convert to PCM16 and send to encoders
        var pcmBytes = AudioSampleConverter.FloatToPcm16(samples);

        foreach (var encoder in _encoders)
        {
            encoder.WriteAudioData(pcmBytes);
        }
    }

    private void OnEncoderExited(object? sender, EventArgs e)
    {
        // Skip if we're already stopping or encoders are being destroyed
        if (!_isRunning || !_isEncodingActive || _encoders.Count == 0) return;

        try
        {
            // Check if all encoders have stopped
            if (_encoders.All(enc => !enc.IsRunning))
            {
                Stop();
            }
        }
        catch (InvalidOperationException)
        {
            // Encoder was disposed while checking, ignore
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

        // Finalize debug WAV file
        FinalizeDebugWavFile();

        foreach (var encoder in _encoders)
        {
            encoder.Stop();
        }

        // Clean up stream output folder (remove old segments/playlists but keep debug files)
        CleanupStreamOutputFolder();

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupStreamOutputFolder()
    {
        if (string.IsNullOrEmpty(_streamOutputDir) || !Directory.Exists(_streamOutputDir))
            return;

        try
        {
            var filesToDelete = Directory.GetFiles(_streamOutputDir)
                .Where(f =>
                {
                    var fileName = Path.GetFileName(f).ToLowerInvariant();
                    // Keep debug WAV files
                    if (fileName.StartsWith("debug_") && fileName.EndsWith(".wav"))
                        return false;
                    // Delete segments, playlists, manifests, init files
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".ts" || ext == ".m4s" || ext == ".mp4" ||
                           ext == ".m3u8" || ext == ".mpd";
                })
                .ToList();

            foreach (var file in filesToDelete)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Failed to delete {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (filesToDelete.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Cleaned up {filesToDelete.Count} stream files");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Failed to cleanup stream folder: {ex.Message}");
        }
    }

    private void FinalizeDebugWavFile()
    {
        if (_debugBeforeVstStream == null) return;

        try
        {
            // Seek back and update WAV header with correct size
            _debugBeforeVstStream.Seek(0, SeekOrigin.Begin);
            WriteWavHeader(_debugBeforeVstStream, _actualSampleRate, _actualChannels, (int)_debugBeforeVstBytes);
            _debugBeforeVstStream.Close();
            _debugBeforeVstStream.Dispose();
            _debugBeforeVstStream = null;
            System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Debug before-VST file finalized: {_debugBeforeVstBytes} bytes");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{_config.Name}] Failed to finalize debug WAV: {ex.Message}");
        }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, int channels, int dataSize)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize); // File size - 8
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size (16 for PCM)
        writer.Write((short)1); // AudioFormat (1 = PCM)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);

        // data subchunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
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
