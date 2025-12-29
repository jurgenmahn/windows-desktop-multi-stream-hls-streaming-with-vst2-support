namespace AudioProcessorAndStreamer.Infrastructure;

public static class AudioSampleConverter
{
    public static float[] BytesToFloat(byte[] bytes, int bytesPerSample = 2)
    {
        int sampleCount = bytes.Length / bytesPerSample;
        var samples = new float[sampleCount];

        if (bytesPerSample == 2) // 16-bit PCM
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = sample / 32768f;
            }
        }
        else if (bytesPerSample == 4) // 32-bit float
        {
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        }
        else if (bytesPerSample == 3) // 24-bit PCM
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);
                samples[i] = sample / 8388608f;
            }
        }

        return samples;
    }

    public static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];

        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short sample = (short)(clamped * 32767);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    public static byte[] FloatToPcm24(float[] samples)
    {
        var bytes = new byte[samples.Length * 3];

        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            int sample = (int)(clamped * 8388607);
            bytes[i * 3] = (byte)(sample & 0xFF);
            bytes[i * 3 + 1] = (byte)((sample >> 8) & 0xFF);
            bytes[i * 3 + 2] = (byte)((sample >> 16) & 0xFF);
        }

        return bytes;
    }

    public static float[] InterleaveStereo(float[] left, float[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        var interleaved = new float[length * 2];

        for (int i = 0; i < length; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }

        return interleaved;
    }

    public static (float[] left, float[] right) DeinterleaveStereo(float[] interleaved)
    {
        int length = interleaved.Length / 2;
        var left = new float[length];
        var right = new float[length];

        for (int i = 0; i < length; i++)
        {
            left[i] = interleaved[i * 2];
            right[i] = interleaved[i * 2 + 1];
        }

        return (left, right);
    }
}
