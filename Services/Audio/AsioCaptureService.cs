using System.Runtime.InteropServices;
using AudioProcessorAndStreamer.Models;
using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

public class AsioCaptureService : IAudioCaptureService
{
    private readonly AsioOut _asioOut;
    private readonly WaveFormat _waveFormat;
    private readonly int _inputChannelOffset;
    private readonly int _inputChannelCount;
    private bool _isCapturing;
    private bool _disposed;

    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public WaveFormat WaveFormat => _waveFormat;
    public bool IsCapturing => _isCapturing;

    public AsioCaptureService(AudioInputConfig config)
    {
        var driverNames = AsioOut.GetDriverNames();
        string driverName;

        if (!string.IsNullOrEmpty(config.DeviceId) && driverNames.Contains(config.DeviceId))
        {
            driverName = config.DeviceId;
        }
        else if (driverNames.Length > 0)
        {
            driverName = driverNames[0];
        }
        else
        {
            throw new InvalidOperationException("No ASIO drivers found");
        }

        _asioOut = new AsioOut(driverName);
        _inputChannelOffset = 0;
        _inputChannelCount = Math.Min(config.Channels, _asioOut.DriverInputChannelCount);

        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(config.SampleRate, _inputChannelCount);

        _asioOut.AudioAvailable += OnAudioAvailable;
        _asioOut.InitRecordAndPlayback(null, _inputChannelCount, config.SampleRate);
    }

    public AsioCaptureService(string driverName, int sampleRate = 48000, int channels = 2)
    {
        _asioOut = new AsioOut(driverName);
        _inputChannelOffset = 0;
        _inputChannelCount = Math.Min(channels, _asioOut.DriverInputChannelCount);

        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _inputChannelCount);

        _asioOut.AudioAvailable += OnAudioAvailable;
        _asioOut.InitRecordAndPlayback(null, _inputChannelCount, sampleRate);
    }

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        int samplesPerChannel = e.SamplesPerBuffer;
        var interleavedSamples = new float[samplesPerChannel * _inputChannelCount];

        for (int channel = 0; channel < _inputChannelCount; channel++)
        {
            var channelBuffer = new float[samplesPerChannel];
            Marshal.Copy(e.InputBuffers[_inputChannelOffset + channel], channelBuffer, 0, samplesPerChannel);

            for (int sample = 0; sample < samplesPerChannel; sample++)
            {
                interleavedSamples[sample * _inputChannelCount + channel] = channelBuffer[sample];
            }
        }

        DataAvailable?.Invoke(this, new AudioDataEventArgs(interleavedSamples, samplesPerChannel, _inputChannelCount));

        e.WrittenToOutputBuffers = false;
    }

    public void StartCapture()
    {
        if (_isCapturing) return;
        _asioOut.Play();
        _isCapturing = true;
    }

    public void StopCapture()
    {
        if (!_isCapturing) return;
        _asioOut.Stop();
        _isCapturing = false;
    }

    public void ShowControlPanel()
    {
        _asioOut.ShowControlPanel();
    }

    public static string[] GetDriverNames()
    {
        return AsioOut.GetDriverNames();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();
        _asioOut.AudioAvailable -= OnAudioAvailable;
        _asioOut.Dispose();
    }
}
