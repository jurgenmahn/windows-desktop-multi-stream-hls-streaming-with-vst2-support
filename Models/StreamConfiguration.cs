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

    /// <summary>
    /// The streaming format to use (HLS or DASH).
    /// </summary>
    public StreamFormat StreamFormat { get; set; } = StreamFormat.Hls;

    /// <summary>
    /// The container format for segments (MPEG-TS or fMP4).
    /// Note: DASH requires fMP4. MPEG-TS is only available for HLS.
    /// </summary>
    public ContainerFormat ContainerFormat { get; set; } = ContainerFormat.MpegTs;
}

/// <summary>
/// Streaming protocol format.
/// </summary>
public enum StreamFormat
{
    /// <summary>
    /// HTTP Live Streaming (Apple) - uses .m3u8 playlists
    /// </summary>
    Hls,

    /// <summary>
    /// Dynamic Adaptive Streaming over HTTP (MPEG) - uses .mpd manifests
    /// </summary>
    Dash
}

/// <summary>
/// Container format for stream segments.
/// </summary>
public enum ContainerFormat
{
    /// <summary>
    /// MPEG Transport Stream (.ts segments) - only compatible with HLS
    /// </summary>
    MpegTs,

    /// <summary>
    /// Fragmented MP4 (.m4s segments) - compatible with both HLS and DASH
    /// </summary>
    Fmp4
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
    public string PluginPath { get; set; } = "Plugins/vst_stereo_tool_64.dll";
    public string PluginName { get; set; } = "Stereo Tool";
    public int Order { get; set; }
    public bool IsBypassed { get; set; }
    public string? PresetFilePath { get; set; }
    public byte[]? PresetData { get; set; }
}
