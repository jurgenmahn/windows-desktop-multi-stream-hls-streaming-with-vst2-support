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

    /// <summary>
    /// Called when a listener connects to a stream. Starts encoding if lazy processing is enabled.
    /// </summary>
    void OnListenerConnected(string streamPath);

    /// <summary>
    /// Called when all listeners disconnect from a stream. Schedules encoding stop if lazy processing is enabled.
    /// </summary>
    void OnNoListeners(string streamPath);
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
