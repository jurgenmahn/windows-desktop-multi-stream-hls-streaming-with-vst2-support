namespace AudioProcessorAndStreamer.Models;

public class EncodingProfile
{
    public string Name { get; set; } = "Default";
    public AudioCodec Codec { get; set; } = AudioCodec.Aac;
    public int Bitrate { get; set; } = 128000;
    public int SampleRate { get; set; } = 48000;
    public int SegmentDuration { get; set; } = 4;
    public int PlaylistSize { get; set; } = 5;
}

public enum AudioCodec
{
    Aac,
    Mp3,
    Opus
}
