using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using AudioProcessorAndStreamer.Models;

namespace AudioProcessorAndStreamer.Services.Encoding;

public class FfmpegProcessManager : IDisposable
{
    private readonly Process _ffmpegProcess;
    private readonly Stream _inputStream;
    private readonly BlockingCollection<byte[]> _writeQueue;
    private readonly Thread _writerThread;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    public EncodingProfile Profile { get; }
    public string OutputPath { get; }
    public bool IsRunning => !_ffmpegProcess.HasExited;

    public event EventHandler<string>? ErrorDataReceived;
    public event EventHandler? ProcessExited;

    public FfmpegProcessManager(
        string ffmpegPath,
        EncodingProfile profile,
        string outputPath,
        int inputSampleRate,
        int inputChannels)
    {
        Profile = profile;
        OutputPath = outputPath;

        var arguments = BuildArguments(profile, outputPath, inputSampleRate, inputChannels);

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };

        _ffmpegProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                ErrorDataReceived?.Invoke(this, e.Data);
            }
        };

        _ffmpegProcess.Exited += (s, e) =>
        {
            ProcessExited?.Invoke(this, EventArgs.Empty);
        };

        _writeQueue = new BlockingCollection<byte[]>(boundedCapacity: 100);
        _cts = new CancellationTokenSource();

        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();
        _inputStream = _ffmpegProcess.StandardInput.BaseStream;

        _writerThread = new Thread(WriterLoop)
        {
            Name = $"FFmpeg Writer - {profile.Name}",
            IsBackground = true
        };
        _writerThread.Start();
    }

    private static string BuildArguments(
        EncodingProfile profile,
        string outputPath,
        int inputSampleRate,
        int inputChannels)
    {
        var codec = profile.Codec switch
        {
            AudioCodec.Aac => "aac",
            AudioCodec.Mp3 => "libmp3lame",
            AudioCodec.Opus => "libopus",
            _ => "aac"
        };

        var bitrateK = profile.Bitrate / 1000;

        // Input: raw PCM 16-bit signed little-endian
        // Output: HLS with specified codec
        var args = $"-hide_banner -loglevel warning " +
                   $"-f s16le -ar {inputSampleRate} -ac {inputChannels} -i - " +
                   $"-c:a {codec} -b:a {bitrateK}k ";

        // Add codec-specific options
        if (profile.Codec == AudioCodec.Aac)
        {
            args += "-aac_coder twoloop ";
        }
        else if (profile.Codec == AudioCodec.Mp3)
        {
            args += "-q:a 2 ";
        }

        // Resample if needed
        if (profile.SampleRate != inputSampleRate)
        {
            args += $"-ar {profile.SampleRate} ";
        }

        // HLS output options
        args += $"-f hls " +
                $"-hls_time {profile.SegmentDuration} " +
                $"-hls_list_size {profile.PlaylistSize} " +
                $"-hls_flags delete_segments+append_list " +
                $"-hls_segment_filename \"{Path.Combine(Path.GetDirectoryName(outputPath)!, "segment_%03d.ts")}\" " +
                $"\"{outputPath}\"";

        return args;
    }

    private void WriterLoop()
    {
        try
        {
            foreach (var data in _writeQueue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    _inputStream.Write(data, 0, data.Length);
                    _inputStream.Flush();
                }
                catch (IOException)
                {
                    // FFmpeg process closed
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    public void WriteAudioData(byte[] pcmData)
    {
        if (_disposed || _ffmpegProcess.HasExited)
            return;

        try
        {
            // Non-blocking add with timeout
            _writeQueue.TryAdd(pcmData, 100);
        }
        catch (ObjectDisposedException)
        {
            // Queue was disposed
        }
        catch (InvalidOperationException)
        {
            // Queue was marked as complete for adding (during shutdown)
        }
    }

    public void Stop()
    {
        if (_disposed) return;

        _cts.Cancel();
        _writeQueue.CompleteAdding();

        try
        {
            _inputStream.Close();
        }
        catch { }

        if (!_ffmpegProcess.HasExited)
        {
            _ffmpegProcess.WaitForExit(5000);
            if (!_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _cts.Dispose();
        _writeQueue.Dispose();
        _ffmpegProcess.Dispose();
    }
}
