using AudioProcessorAndStreamer.Models;

namespace AudioProcessorAndStreamer.Services.Encoding;

public interface IFfmpegService
{
    FfmpegProcessManager CreateEncoder(
        EncodingProfile profile,
        string outputPath,
        int inputSampleRate,
        int inputChannels,
        int hlsSegmentDuration,
        int hlsPlaylistSize,
        StreamFormat streamFormat = StreamFormat.Hls,
        ContainerFormat containerFormat = ContainerFormat.MpegTs,
        bool debugAudioEnabled = false);

    bool IsAvailable { get; }
    string FfmpegPath { get; }
}
