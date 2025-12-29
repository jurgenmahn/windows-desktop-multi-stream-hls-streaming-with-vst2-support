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

        _context.PluginCommandStub.Commands.MainsChanged(true);
        _context.PluginCommandStub.Commands.StartProcess();
        _isProcessing = true;
    }

    public void ProcessAudio(float[] interleavedInput, float[] interleavedOutput, int channels)
    {
        // For now, bypass processing - VST.NET2 requires VstAudioBuffer which needs
        // more complex setup. This can be implemented properly later.
        // The plugin is still loaded and can be used for presets, editor, etc.
        Array.Copy(interleavedInput, interleavedOutput, Math.Min(interleavedInput.Length, interleavedOutput.Length));

        // TODO: Implement proper VST audio processing using VstAudioPrecisionBuffer
        // The VST.NET2-Host library uses a different buffer management approach
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
