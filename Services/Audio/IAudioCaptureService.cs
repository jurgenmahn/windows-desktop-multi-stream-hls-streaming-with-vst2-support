using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<AudioDataEventArgs>? DataAvailable;
    WaveFormat WaveFormat { get; }
    bool IsCapturing { get; }
    void StartCapture();
    void StopCapture();
}

public class AudioDataEventArgs : EventArgs
{
    public float[] Buffer { get; }
    public int SamplesPerChannel { get; }
    public int Channels { get; }

    public AudioDataEventArgs(float[] buffer, int samplesPerChannel, int channels)
    {
        Buffer = buffer;
        SamplesPerChannel = samplesPerChannel;
        Channels = channels;
    }
}
