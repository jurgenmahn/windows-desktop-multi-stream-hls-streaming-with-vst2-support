using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

/// <summary>
/// Lightweight audio monitor for visualizing input without full stream processing.
/// </summary>
public class AudioInputMonitor : IDisposable
{
    private readonly IAudioCaptureService _capture;
    private bool _isMonitoring;
    private bool _disposed;

    public event EventHandler<float[]>? SamplesAvailable;

    public bool IsMonitoring => _isMonitoring;

    public AudioInputMonitor(AudioInputConfig config)
    {
        _capture = config.DriverType switch
        {
            AudioDriverType.Asio => new AsioCaptureService(config),
            _ => new WasapiCaptureService(config)
        };

        _capture.DataAvailable += OnDataAvailable;
    }

    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isMonitoring) return;

        // Copy samples to avoid reference issues
        var samples = new float[e.Buffer.Length];
        Array.Copy(e.Buffer, samples, e.Buffer.Length);

        SamplesAvailable?.Invoke(this, samples);
    }

    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        _capture.StartCapture();
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;
        _capture.StopCapture();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }
}
