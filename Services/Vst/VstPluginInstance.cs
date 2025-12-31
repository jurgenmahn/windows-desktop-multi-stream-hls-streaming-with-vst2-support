using System.IO;
using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;

namespace AudioProcessorAndStreamer.Services.Vst;

public class VstPluginInstance : IDisposable
{
    private readonly IVstPluginContext _context;
    private readonly HostCommandStub _hostStub;
    private int _blockSize;
    private int _sampleRate;
    private bool _isProcessing;
    private bool _disposed;

    // Audio buffers for VST processing (deinterleaved)
    private float[][]? _inputChannelBuffers;
    private float[][]? _outputChannelBuffers;
    private int _allocatedBlockSize;

    public string PluginPath { get; }
    public string PluginName { get; }
    public int InputCount { get; }
    public int OutputCount { get; }
    public bool HasEditor { get; }
    public bool IsBypassed { get; set; }

    public VstPluginInstance(IVstPluginContext context, HostCommandStub hostStub, string pluginPath)
    {
        _context = context;
        _hostStub = hostStub;
        PluginPath = pluginPath;

        var pluginInfo = context.PluginInfo;
        PluginName = Path.GetFileNameWithoutExtension(pluginPath);
        InputCount = pluginInfo.AudioInputCount;
        OutputCount = pluginInfo.AudioOutputCount;
        HasEditor = pluginInfo.Flags.HasFlag(VstPluginFlags.HasEditor);
    }

    public void Initialize(int sampleRate, int blockSize)
    {
        _sampleRate = sampleRate;
        _blockSize = blockSize;
        _hostStub.CurrentSampleRate = sampleRate;
        _hostStub.CurrentBlockSize = blockSize;

        _context.PluginCommandStub.Commands.SetSampleRate((float)sampleRate);
        _context.PluginCommandStub.Commands.SetBlockSize(blockSize);

        // Allocate audio buffers
        AllocateBuffers(blockSize);

        _context.PluginCommandStub.Commands.MainsChanged(true);
        _context.PluginCommandStub.Commands.StartProcess();
        _isProcessing = true;
    }

    private void AllocateBuffers(int blockSize)
    {
        if (_allocatedBlockSize >= blockSize && _inputChannelBuffers != null)
            return;

        _allocatedBlockSize = blockSize;

        // Allocate input buffers (at least 2 channels for stereo)
        int inputChannels = Math.Max(2, InputCount);
        _inputChannelBuffers = new float[inputChannels][];
        for (int i = 0; i < inputChannels; i++)
        {
            _inputChannelBuffers[i] = new float[blockSize];
        }

        // Allocate output buffers
        int outputChannels = Math.Max(2, OutputCount);
        _outputChannelBuffers = new float[outputChannels][];
        for (int i = 0; i < outputChannels; i++)
        {
            _outputChannelBuffers[i] = new float[blockSize];
        }
    }

    private static int _processDebugCounter;

    public unsafe void ProcessAudio(float[] interleavedInput, float[] interleavedOutput, int channels)
    {
        if (IsBypassed || _inputChannelBuffers == null || _outputChannelBuffers == null)
        {
            if (_processDebugCounter++ % 500 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VST {PluginName}] BYPASSED: IsBypassed={IsBypassed}, " +
                    $"inputBuffers={_inputChannelBuffers != null}, outputBuffers={_outputChannelBuffers != null}");
            }
            Array.Copy(interleavedInput, interleavedOutput, Math.Min(interleavedInput.Length, interleavedOutput.Length));
            return;
        }

        int samplesPerChannel = interleavedInput.Length / channels;

        // Ensure buffers are large enough
        if (samplesPerChannel > _allocatedBlockSize)
        {
            AllocateBuffers(samplesPerChannel);
        }

        // Deinterleave input: LRLRLR -> L[], R[]
        for (int ch = 0; ch < Math.Min(channels, _inputChannelBuffers.Length); ch++)
        {
            for (int i = 0; i < samplesPerChannel; i++)
            {
                _inputChannelBuffers[ch][i] = interleavedInput[i * channels + ch];
            }
        }

        // Clear output buffers
        for (int ch = 0; ch < _outputChannelBuffers.Length; ch++)
        {
            Array.Clear(_outputChannelBuffers[ch], 0, samplesPerChannel);
        }

        try
        {
            // Create VstAudioBuffer arrays from our managed arrays using pinned memory
            var inputBuffers = new VstAudioBuffer[_inputChannelBuffers.Length];
            var outputBuffers = new VstAudioBuffer[_outputChannelBuffers.Length];

            // Pin the arrays and create VstAudioBuffers
            var inputHandles = new System.Runtime.InteropServices.GCHandle[_inputChannelBuffers.Length];
            var outputHandles = new System.Runtime.InteropServices.GCHandle[_outputChannelBuffers.Length];

            try
            {
                for (int ch = 0; ch < _inputChannelBuffers.Length; ch++)
                {
                    inputHandles[ch] = System.Runtime.InteropServices.GCHandle.Alloc(_inputChannelBuffers[ch], System.Runtime.InteropServices.GCHandleType.Pinned);
                    float* ptr = (float*)inputHandles[ch].AddrOfPinnedObject();
                    inputBuffers[ch] = new VstAudioBuffer(ptr, samplesPerChannel, false);
                }

                for (int ch = 0; ch < _outputChannelBuffers.Length; ch++)
                {
                    outputHandles[ch] = System.Runtime.InteropServices.GCHandle.Alloc(_outputChannelBuffers[ch], System.Runtime.InteropServices.GCHandleType.Pinned);
                    float* ptr = (float*)outputHandles[ch].AddrOfPinnedObject();
                    outputBuffers[ch] = new VstAudioBuffer(ptr, samplesPerChannel, false);
                }

                // Process through VST plugin
                _context.PluginCommandStub.Commands.ProcessReplacing(inputBuffers, outputBuffers);

                // Debug: check if VST modified the audio
                if (_processDebugCounter++ % 500 == 0)
                {
                    float inSum = 0, outSum = 0;
                    for (int i = 0; i < Math.Min(100, samplesPerChannel); i++)
                    {
                        inSum += Math.Abs(_inputChannelBuffers[0][i]);
                        outSum += Math.Abs(_outputChannelBuffers[0][i]);
                    }
                    System.Diagnostics.Debug.WriteLine($"[VST {PluginName}] ProcessReplacing: inSum={inSum:F4}, outSum={outSum:F4}");
                }
            }
            finally
            {
                // Free pinned handles
                foreach (var handle in inputHandles)
                {
                    if (handle.IsAllocated) handle.Free();
                }
                foreach (var handle in outputHandles)
                {
                    if (handle.IsAllocated) handle.Free();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VST ProcessReplacing failed: {ex.Message}");
            // On failure, copy input to output
            Array.Copy(interleavedInput, interleavedOutput, Math.Min(interleavedInput.Length, interleavedOutput.Length));
            return;
        }

        // Interleave output: L[], R[] -> LRLRLR
        for (int i = 0; i < samplesPerChannel; i++)
        {
            for (int ch = 0; ch < Math.Min(channels, _outputChannelBuffers.Length); ch++)
            {
                interleavedOutput[i * channels + ch] = _outputChannelBuffers[ch][i];
            }
        }
    }

    public byte[]? GetPresetData()
    {
        try
        {
            return _context.PluginCommandStub.Commands.GetChunk(true);
        }
        catch
        {
            return null;
        }
    }

    public void SetPresetData(byte[] data)
    {
        try
        {
            _context.PluginCommandStub.Commands.SetChunk(data, true);
        }
        catch
        {
            // Preset loading failed
        }
    }

    public bool LoadPresetFromFile(string presetFilePath)
    {
        if (string.IsNullOrEmpty(presetFilePath) || !File.Exists(presetFilePath))
            return false;

        try
        {
            var presetData = File.ReadAllBytes(presetFilePath);
            _context.PluginCommandStub.Commands.SetChunk(presetData, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SavePresetToFile(string presetFilePath)
    {
        try
        {
            var presetData = _context.PluginCommandStub.Commands.GetChunk(true);
            if (presetData != null)
            {
                File.WriteAllBytes(presetFilePath, presetData);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void OpenEditor(IntPtr parentHandle)
    {
        if (HasEditor)
        {
            _context.PluginCommandStub.Commands.EditorOpen(parentHandle);
        }
    }

    public void CloseEditor()
    {
        if (HasEditor)
        {
            _context.PluginCommandStub.Commands.EditorClose();
        }
    }

    public System.Drawing.Rectangle GetEditorSize()
    {
        if (HasEditor)
        {
            System.Drawing.Rectangle rect;
            if (_context.PluginCommandStub.Commands.EditorGetRect(out rect))
            {
                return rect;
            }
        }
        return System.Drawing.Rectangle.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isProcessing)
        {
            try
            {
                _context.PluginCommandStub.Commands.StopProcess();
                _context.PluginCommandStub.Commands.MainsChanged(false);
            }
            catch { }
            _isProcessing = false;
        }
    }
}
