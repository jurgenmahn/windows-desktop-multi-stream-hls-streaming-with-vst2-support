using AudioProcessorAndStreamer.Models;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

public interface IMonitorOutputService : IDisposable
{
    /// <summary>
    /// List of available output device names.
    /// </summary>
    IReadOnlyList<string> AvailableDevices { get; }

    /// <summary>
    /// The currently configured output device name.
    /// </summary>
    string CurrentDevice { get; }

    /// <summary>
    /// Whether the monitor is currently active and playing audio.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// The stream ID currently being monitored, or null if none.
    /// </summary>
    string? ActiveStreamId { get; }

    /// <summary>
    /// Event raised when the monitored stream changes.
    /// </summary>
    event EventHandler<string?>? MonitoredStreamChanged;

    /// <summary>
    /// Updates the configured output device. If monitoring is active, it will restart on the new device.
    /// </summary>
    void SetOutputDevice(string deviceName);

    /// <summary>
    /// Start monitoring a specific stream with the specified sample rate and channel count.
    /// Automatically stops monitoring any previous stream.
    /// </summary>
    void StartMonitoring(string streamId, int sampleRate, int channels);

    /// <summary>
    /// Stop monitoring the specified stream. If another stream ID is provided, it's ignored.
    /// </summary>
    void StopMonitoring(string streamId);

    /// <summary>
    /// Stop all monitoring.
    /// </summary>
    void StopAll();

    /// <summary>
    /// Write audio samples to the monitor output (only if the specified stream is being monitored).
    /// </summary>
    void WriteSamples(string streamId, float[] samples);

    /// <summary>
    /// Check if a specific stream is being monitored.
    /// </summary>
    bool IsMonitoring(string streamId);
}

public class MonitorOutputService : IMonitorOutputService
{
    private string _configuredDevice;
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _bufferedProvider;
    private readonly object _lock = new();
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private string? _activeStreamId;

    public IReadOnlyList<string> AvailableDevices { get; }
    public string CurrentDevice => _configuredDevice;
    public bool IsActive => _waveOut != null;
    public string? ActiveStreamId => _activeStreamId;

    public event EventHandler<string?>? MonitoredStreamChanged;

    public MonitorOutputService(IOptions<AppConfiguration> config)
    {
        _configuredDevice = config.Value.MonitorOutputDevice;
        AvailableDevices = GetOutputDevices();

        System.Diagnostics.Debug.WriteLine($"[Monitor] Configured device: '{_configuredDevice}'");
        System.Diagnostics.Debug.WriteLine($"[Monitor] Available devices: {string.Join(", ", AvailableDevices)}");
    }

    public void SetOutputDevice(string deviceName)
    {
        lock (_lock)
        {
            if (_configuredDevice == deviceName) return;

            System.Diagnostics.Debug.WriteLine($"[Monitor] Changing output device from '{_configuredDevice}' to '{deviceName}'");
            _configuredDevice = deviceName;

            // If currently monitoring, restart on new device
            if (_activeStreamId != null && _sampleRate > 0)
            {
                var streamId = _activeStreamId;
                var sampleRate = _sampleRate;
                var channels = _channels;

                StopInternal();
                _activeStreamId = streamId; // Preserve the stream ID

                // Restart on new device
                try
                {
                    var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    NAudio.CoreAudioApi.MMDevice? outputDevice = null;

                    if (!string.IsNullOrEmpty(_configuredDevice))
                    {
                        foreach (var device in enumerator.EnumerateAudioEndPoints(
                            NAudio.CoreAudioApi.DataFlow.Render,
                            NAudio.CoreAudioApi.DeviceState.Active))
                        {
                            if (device.FriendlyName == _configuredDevice)
                            {
                                outputDevice = device;
                                break;
                            }
                        }
                    }

                    outputDevice ??= enumerator.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.Role.Multimedia);

                    System.Diagnostics.Debug.WriteLine($"[Monitor] Switched to output device: {outputDevice.FriendlyName}");

                    var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                    _bufferedProvider = new BufferedWaveProvider(waveFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(1),
                        DiscardOnBufferOverflow = true
                    };

                    _waveOut = new WasapiOut(outputDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
                    _waveOut.Init(_bufferedProvider);
                    _waveOut.Play();

                    _sampleRate = sampleRate;
                    _channels = channels;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Monitor] Failed to switch device: {ex.Message}");
                    _activeStreamId = null;
                }
            }
        }
    }

    private static List<string> GetOutputDevices()
    {
        var devices = new List<string>();

        // Get WASAPI output devices
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(
            NAudio.CoreAudioApi.DataFlow.Render,
            NAudio.CoreAudioApi.DeviceState.Active))
        {
            devices.Add(device.FriendlyName);
        }

        return devices;
    }

    public void StartMonitoring(string streamId, int sampleRate, int channels)
    {
        lock (_lock)
        {
            if (_disposed) return;

            // Stop any existing playback
            StopInternal();

            _activeStreamId = streamId;
            _sampleRate = sampleRate;
            _channels = channels;

            try
            {
                // Find the output device
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                NAudio.CoreAudioApi.MMDevice? outputDevice = null;

                if (!string.IsNullOrEmpty(_configuredDevice))
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        if (device.FriendlyName == _configuredDevice)
                        {
                            outputDevice = device;
                            break;
                        }
                    }
                }

                // Fall back to default device if not found
                outputDevice ??= enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);

                System.Diagnostics.Debug.WriteLine($"[Monitor] Using output device: {outputDevice.FriendlyName}");

                // Create buffered provider for the audio format
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                _bufferedProvider = new BufferedWaveProvider(waveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true
                };

                // Create WASAPI output
                _waveOut = new WasapiOut(outputDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
                _waveOut.Init(_bufferedProvider);
                _waveOut.Play();

                System.Diagnostics.Debug.WriteLine($"[Monitor] Started for stream '{streamId}': {sampleRate}Hz, {channels}ch");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Monitor] Failed to start: {ex.Message}");
                StopInternal();
            }
        }

        // Notify listeners outside the lock
        MonitoredStreamChanged?.Invoke(this, _activeStreamId);
    }

    public void StopMonitoring(string streamId)
    {
        lock (_lock)
        {
            // Only stop if this stream is actually being monitored
            if (_activeStreamId != streamId) return;
            StopInternal();
        }

        MonitoredStreamChanged?.Invoke(this, null);
    }

    public void StopAll()
    {
        lock (_lock)
        {
            StopInternal();
        }

        MonitoredStreamChanged?.Invoke(this, null);
    }

    private void StopInternal()
    {
        if (_waveOut != null)
        {
            try
            {
                _waveOut.Stop();
                _waveOut.Dispose();
            }
            catch { }
            _waveOut = null;
        }

        _bufferedProvider = null;
        var wasActive = _activeStreamId;
        _activeStreamId = null;

        if (wasActive != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Monitor] Stopped monitoring stream '{wasActive}'");
        }
    }

    public void WriteSamples(string streamId, float[] samples)
    {
        lock (_lock)
        {
            // Only accept samples from the active stream
            if (_activeStreamId != streamId || _bufferedProvider == null || _waveOut == null) return;

            try
            {
                // Convert float samples to bytes (IEEE float format)
                var byteCount = samples.Length * sizeof(float);
                var bytes = new byte[byteCount];
                Buffer.BlockCopy(samples, 0, bytes, 0, byteCount);

                _bufferedProvider.AddSamples(bytes, 0, byteCount);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Monitor] Write error: {ex.Message}");
            }
        }
    }

    public bool IsMonitoring(string streamId)
    {
        lock (_lock)
        {
            return _activeStreamId == streamId;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAll();
    }
}
