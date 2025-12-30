namespace AudioProcessorAndStreamer.Infrastructure;

/// <summary>
/// A smoothing buffer that accumulates bursty audio input and outputs
/// fixed-size chunks at regular intervals. Helps regulate VST output
/// that arrives in variable-sized bursts.
/// </summary>
public class AudioSmoothingBuffer
{
    private readonly float[] _buffer;
    private readonly int _bufferSize;
    private readonly int _outputChunkSize;
    private readonly object _lock = new();
    private int _writePosition;
    private int _readPosition;
    private int _availableSamples;

    public event EventHandler<float[]>? ChunkReady;

    /// <summary>
    /// Creates a smoothing buffer.
    /// </summary>
    /// <param name="bufferSeconds">Total buffer capacity in seconds</param>
    /// <param name="outputChunkMs">Output chunk size in milliseconds</param>
    /// <param name="sampleRate">Audio sample rate</param>
    /// <param name="channels">Number of audio channels</param>
    public AudioSmoothingBuffer(double bufferSeconds, int outputChunkMs, int sampleRate, int channels)
    {
        _bufferSize = (int)(bufferSeconds * sampleRate * channels);
        _outputChunkSize = (int)(outputChunkMs / 1000.0 * sampleRate * channels);
        _buffer = new float[_bufferSize];
        _writePosition = 0;
        _readPosition = 0;
        _availableSamples = 0;
    }

    private int _writeCount = 0;
    private int _chunkCount = 0;

    /// <summary>
    /// Write samples to the buffer. If enough samples are available,
    /// ChunkReady events will be fired with fixed-size output chunks.
    /// </summary>
    public void Write(float[] samples)
    {
        if (samples.Length == 0) return;

        List<float[]>? chunksToFire = null;

        lock (_lock)
        {
            _writeCount++;
            if (_writeCount % 100 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[SmoothingBuffer] Write #{_writeCount}: {samples.Length} samples, available={_availableSamples}, chunkSize={_outputChunkSize}");
            }

            // Write samples to circular buffer
            int samplesToWrite = Math.Min(samples.Length, _bufferSize - _availableSamples);

            for (int i = 0; i < samplesToWrite; i++)
            {
                _buffer[_writePosition] = samples[i];
                _writePosition = (_writePosition + 1) % _bufferSize;
            }

            _availableSamples += samplesToWrite;

            // Collect all chunks to fire (outside of lock)
            while (_availableSamples >= _outputChunkSize)
            {
                var chunk = new float[_outputChunkSize];
                for (int i = 0; i < _outputChunkSize; i++)
                {
                    chunk[i] = _buffer[_readPosition];
                    _readPosition = (_readPosition + 1) % _bufferSize;
                }
                _availableSamples -= _outputChunkSize;

                chunksToFire ??= new List<float[]>();
                chunksToFire.Add(chunk);
            }
        }

        // Fire events outside lock to avoid deadlocks
        if (chunksToFire != null)
        {
            var handler = ChunkReady;
            if (handler != null)
            {
                foreach (var chunk in chunksToFire)
                {
                    _chunkCount++;
                    if (_chunkCount % 500 == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SmoothingBuffer] ChunkReady #{_chunkCount}: {chunk.Length} samples");
                    }
                    handler.Invoke(this, chunk);
                }
            }
        }
    }

    /// <summary>
    /// Gets the current buffer fill level as a percentage (0-100).
    /// </summary>
    public double FillLevel
    {
        get
        {
            lock (_lock)
            {
                return (_availableSamples / (double)_bufferSize) * 100;
            }
        }
    }

    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _writePosition = 0;
            _readPosition = 0;
            _availableSamples = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }
}
