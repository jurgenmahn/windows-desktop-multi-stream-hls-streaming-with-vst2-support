namespace AudioProcessorAndStreamer.Models;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AudioDriverType DriverType { get; set; }
    public int Channels { get; set; }
    public int SampleRate { get; set; }
}
