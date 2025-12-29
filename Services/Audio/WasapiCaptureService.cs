using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

public class WasapiCaptureService : IAudioCaptureService
{
    private readonly WasapiCapture _capture;
    private readonly WaveFormat _waveFormat;
    private bool _isCapturing;
    private bool _disposed;

    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public WaveFormat WaveFormat => _waveFormat;
    public bool IsCapturing => _isCapturing;

    public WasapiCaptureService(AudioInputConfig config)
    {
        MMDevice? device = null;

        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(config.DeviceId))
        {
            try
            {
                device = enumerator.GetDevice(config.DeviceId);
            }
            catch
            {
                device = null;
            }
        }

        device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

        _capture = new WasapiCapture(device, true, config.BufferSize);
        _waveFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
    }

    public WasapiCaptureService(MMDevice device, int bufferMilliseconds = 100)
    {
        _capture = new WasapiCapture(device, true, bufferMilliseconds);
        _waveFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        int bytesPerSample = _waveFormat.BitsPerSample / 8;
        int channels = _waveFormat.Channels;

        float[] samples;

        if (_waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else
        {
            samples = AudioSampleConverter.BytesToFloat(
                e.Buffer.AsSpan(0, e.BytesRecorded).ToArray(),
                bytesPerSample);
        }

        int samplesPerChannel = samples.Length / channels;
        DataAvailable?.Invoke(this, new AudioDataEventArgs(samples, samplesPerChannel, channels));
    }

    public void StartCapture()
    {
        if (_isCapturing) return;
        _capture.StartRecording();
        _isCapturing = true;
    }

    public void StopCapture()
    {
        if (!_isCapturing) return;
        _capture.StopRecording();
        _isCapturing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }
}
