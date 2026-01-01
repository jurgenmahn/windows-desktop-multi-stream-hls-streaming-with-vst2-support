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
    private readonly bool _debugAudioEnabled;
    private readonly FileStream? _debugInputStream;
    private readonly int _debugSampleRate;
    private readonly int _debugChannels;
    private readonly int _hlsSegmentDuration;
    private readonly int _hlsPlaylistSize;
    private readonly StreamFormat _streamFormat;
    private readonly ContainerFormat _containerFormat;
    private long _debugBytesWritten;
    private bool _disposed;

    public EncodingProfile Profile { get; }
    public string OutputPath { get; }
    public StreamFormat StreamFormat => _streamFormat;
    public ContainerFormat ContainerFormat => _containerFormat;
    public bool IsRunning => !_ffmpegProcess.HasExited;

    public event EventHandler<string>? ErrorDataReceived;
    public event EventHandler? ProcessExited;

    public FfmpegProcessManager(
        string ffmpegPath,
        EncodingProfile profile,
        string outputPath,
        int inputSampleRate,
        int inputChannels,
        int hlsSegmentDuration,
        int hlsPlaylistSize,
        StreamFormat streamFormat = StreamFormat.Hls,
        ContainerFormat containerFormat = ContainerFormat.MpegTs,
        bool debugAudioEnabled = false)
    {
        Profile = profile;
        OutputPath = outputPath;
        _debugAudioEnabled = debugAudioEnabled;
        _debugSampleRate = inputSampleRate;
        _debugChannels = inputChannels;
        _hlsSegmentDuration = hlsSegmentDuration;
        _hlsPlaylistSize = hlsPlaylistSize;
        _streamFormat = streamFormat;
        _containerFormat = containerFormat;

        var arguments = BuildArguments(profile, outputPath, inputSampleRate, inputChannels, hlsSegmentDuration, hlsPlaylistSize, streamFormat, containerFormat, debugAudioEnabled);

        // Delete existing debug_output.wav file if it exists (for clean start)
        if (debugAudioEnabled && profile.Name.Contains("192"))
        {
            var debugOutputPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "debug_output.wav");
            try
            {
                if (File.Exists(debugOutputPath))
                {
                    File.Delete(debugOutputPath);
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg] Deleted existing debug_output.wav");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] Failed to delete debug_output.wav: {ex.Message}");
            }
        }

        // Set working directory to output folder so init files are created there
        var outputDir = Path.GetDirectoryName(outputPath)!;

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = outputDir,
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
                System.Diagnostics.Debug.WriteLine($"[FFmpeg {profile.Name}] {e.Data}");
                ErrorDataReceived?.Invoke(this, e.Data);
            }
        };

        _ffmpegProcess.Exited += (s, e) =>
        {
            ProcessExited?.Invoke(this, EventArgs.Empty);
        };

        _writeQueue = new BlockingCollection<byte[]>(boundedCapacity: 100);
        _cts = new CancellationTokenSource();

        // Create debug input WAV file (only for first profile to avoid duplicates)
        if (_debugAudioEnabled && profile.Name.Contains("192"))
        {
            var debugDir = Path.GetDirectoryName(outputPath)!;
            var debugInputPath = Path.Combine(debugDir, "debug_input.wav");
            try
            {
                // Delete existing file to ensure clean start
                if (File.Exists(debugInputPath))
                {
                    File.Delete(debugInputPath);
                }
                _debugInputStream = new FileStream(debugInputPath, FileMode.Create, FileAccess.Write);
                // Write placeholder WAV header (will be updated on close)
                WriteWavHeader(_debugInputStream, inputSampleRate, inputChannels, 0);
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] Debug input file: {debugInputPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] Failed to create debug input file: {ex.Message}");
            }
        }

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
        int inputChannels,
        int hlsSegmentDuration,
        int hlsPlaylistSize,
        StreamFormat streamFormat,
        ContainerFormat containerFormat,
        bool debugAudioEnabled)
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
        System.Diagnostics.Debug.WriteLine($"[FFmpeg] Creating encoder: {profile.Name}, format={streamFormat}/{containerFormat}, input={inputSampleRate}Hz/{inputChannels}ch, output={profile.SampleRate}Hz");

        // -y: overwrite output files without asking
        // -thread_queue_size: buffer input to handle irregular data delivery
        // -af aresample=async=1: fix timing discontinuities by stretching/compressing audio
        var args = $"-y -hide_banner -loglevel warning " +
                   $"-thread_queue_size 4096 " +
                   $"-f s16le -ar {inputSampleRate} -ac {inputChannels} -i - " +
                   $"-af aresample=async=1:first_pts=0 " +
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

        var profileBaseName = Path.GetFileNameWithoutExtension(outputPath);
        var outputDir = Path.GetDirectoryName(outputPath)!;

        if (streamFormat == StreamFormat.Dash)
        {
            // DASH output format (always uses fMP4)
            // For DASH, outputPath should be the .mpd manifest file
            var mpdPath = Path.Combine(outputDir, $"{profileBaseName}.mpd");
            var initPattern = Path.Combine(outputDir, $"{profileBaseName}_init.m4s");
            var mediaPattern = Path.Combine(outputDir, $"{profileBaseName}_$Number$.m4s");

            args += $"-f dash " +
                    $"-seg_duration {hlsSegmentDuration} " +
                    $"-window_size {hlsPlaylistSize} " +
                    $"-extra_window_size 0 " +
                    $"-remove_at_exit 0 " +
                    $"-use_template 1 " +
                    $"-use_timeline 1 " +
                    $"-init_seg_name \"{Path.GetFileName(initPattern)}\" " +
                    $"-media_seg_name \"{profileBaseName}_$Number$.m4s\" " +
                    $"-adaptation_sets \"id=0,streams=a\" " +
                    $"\"{mpdPath}\"";
        }
        else
        {
            // HLS output format
            string segmentExtension;
            string hlsSegmentType = "";

            if (containerFormat == ContainerFormat.Fmp4)
            {
                // HLS with fMP4 segments
                segmentExtension = "m4s";
                // fMP4 init segment filename (relative to playlist directory)
                var initFilename = $"{profileBaseName}_init.mp4";
                hlsSegmentType = $"-hls_segment_type fmp4 " +
                                 $"-hls_fmp4_init_filename \"{initFilename}\" ";
            }
            else
            {
                // HLS with MPEG-TS segments (default)
                segmentExtension = "ts";
            }

            var segmentPattern = Path.Combine(outputDir, $"{profileBaseName}_%03d.{segmentExtension}");

            args += $"-f hls " +
                    $"-hls_time {hlsSegmentDuration} " +
                    $"-hls_list_size {hlsPlaylistSize} " +
                    hlsSegmentType +
                    $"-hls_flags delete_segments+append_list" +
                    (containerFormat == ContainerFormat.Fmp4 ? "+independent_segments" : "") + " " +
                    $"-hls_segment_filename \"{segmentPattern}\" " +
                    $"\"{outputPath}\"";
        }

        // Add debug WAV output (only for 192kbps profile to avoid duplicates)
        if (debugAudioEnabled && profile.Name.Contains("192"))
        {
            var debugOutputPath = Path.Combine(outputDir, "debug_output.wav");
            args += $" -c:a pcm_s16le \"{debugOutputPath}\"";
        }

        System.Diagnostics.Debug.WriteLine($"[FFmpeg] Command: ffmpeg {args}");

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

                    // Write to debug input file if enabled
                    if (_debugInputStream != null)
                    {
                        _debugInputStream.Write(data, 0, data.Length);
                        _debugBytesWritten += data.Length;
                    }
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

    private static void WriteWavHeader(Stream stream, int sampleRate, int channels, int dataSize)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize); // File size - 8
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size (16 for PCM)
        writer.Write((short)1); // AudioFormat (1 = PCM)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);

        // data subchunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
    }

    private void FinalizeDebugWavFile()
    {
        if (_debugInputStream == null) return;

        try
        {
            // Seek back and update WAV header with correct size
            _debugInputStream.Seek(0, SeekOrigin.Begin);
            WriteWavHeader(_debugInputStream, _debugSampleRate, _debugChannels, (int)_debugBytesWritten);
            _debugInputStream.Close();
            _debugInputStream.Dispose();
            System.Diagnostics.Debug.WriteLine($"[FFmpeg] Debug input file finalized: {_debugBytesWritten} bytes");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FFmpeg] Failed to finalize debug WAV: {ex.Message}");
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

        // Finalize debug WAV file
        FinalizeDebugWavFile();

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
