namespace AudioProcessorAndStreamer.Models;

public class StreamConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Untitled Stream";
    public AudioInputConfig AudioInput { get; set; } = new();
    public List<VstPluginConfig> VstPlugins { get; set; } = new();
    public List<EncodingProfile> EncodingProfiles { get; set; } = new();
    public string StreamPath { get; set; } = string.Empty;
    public string? LogoPath { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class AudioInputConfig
{
    public AudioDriverType DriverType { get; set; } = AudioDriverType.Wasapi;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public int BufferSize { get; set; } = 512;
}

public enum AudioDriverType
{
    Wasapi,
    Asio
}

public class VstPluginConfig
{
    public string PluginPath { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsBypassed { get; set; }
    public byte[]? PresetData { get; set; }
}
