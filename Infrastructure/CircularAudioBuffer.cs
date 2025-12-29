namespace AudioProcessorAndStreamer.Infrastructure;

public class CircularAudioBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _writePosition;
    private int _availableSamples;

    public int Capacity { get; }

    public CircularAudioBuffer(int capacity)
    {
        Capacity = capacity;
        _buffer = new float[capacity];
    }

    public void Write(float[] samples)
    {
        lock (_lock)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _buffer[_writePosition] = samples[i];
                _writePosition = (_writePosition + 1) % Capacity;
            }
            _availableSamples = Math.Min(_availableSamples + samples.Length, Capacity);
        }
    }

    public float[] ReadAll()
    {
        lock (_lock)
        {
            var result = new float[_availableSamples];
            int readPosition = (_writePosition - _availableSamples + Capacity) % Capacity;

            for (int i = 0; i < _availableSamples; i++)
            {
                result[i] = _buffer[(readPosition + i) % Capacity];
            }

            return result;
        }
    }

    public float[] ReadLatest(int count)
    {
        lock (_lock)
        {
            int actualCount = Math.Min(count, _availableSamples);
            var result = new float[actualCount];
            int readPosition = (_writePosition - actualCount + Capacity) % Capacity;

            for (int i = 0; i < actualCount; i++)
            {
                result[i] = _buffer[(readPosition + i) % Capacity];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _writePosition = 0;
            _availableSamples = 0;
        }
    }
}
