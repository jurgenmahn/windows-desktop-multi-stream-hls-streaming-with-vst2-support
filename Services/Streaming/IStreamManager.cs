using AudioProcessorAndStreamer.Models;

namespace AudioProcessorAndStreamer.Services.Streaming;

public interface IStreamManager : IDisposable
{
    IReadOnlyDictionary<string, AudioStreamProcessor> ActiveStreams { get; }

    event EventHandler<StreamEventArgs>? StreamStarted;
    event EventHandler<StreamEventArgs>? StreamStopped;
    event EventHandler<StreamErrorEventArgs>? StreamError;

    AudioStreamProcessor? StartStream(StreamConfiguration config);
    void StopStream(string streamId);
    void StopAllStreams();
    AudioStreamProcessor? GetStream(string streamId);
}

public class StreamEventArgs : EventArgs
{
    public string StreamId { get; }
    public string StreamName { get; }

    public StreamEventArgs(string streamId, string streamName)
    {
        StreamId = streamId;
        StreamName = streamName;
    }
}

public class StreamErrorEventArgs : StreamEventArgs
{
    public string ErrorMessage { get; }
    public Exception? Exception { get; }

    public StreamErrorEventArgs(string streamId, string streamName, string errorMessage, Exception? exception = null)
        : base(streamId, streamName)
    {
        ErrorMessage = errorMessage;
        Exception = exception;
    }
}
