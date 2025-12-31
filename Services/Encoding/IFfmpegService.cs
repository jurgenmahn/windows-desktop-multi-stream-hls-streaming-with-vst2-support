using AudioProcessorAndStreamer.Models;

namespace AudioProcessorAndStreamer.Services.Encoding;

public interface IFfmpegService
{
    FfmpegProcessManager CreateEncoder(EncodingProfile profile, string outputPath, int inputSampleRate, int inputChannels, bool debugAudioEnabled = false);
    bool IsAvailable { get; }
    string FfmpegPath { get; }
}
