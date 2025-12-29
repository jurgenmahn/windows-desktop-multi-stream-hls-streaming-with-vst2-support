using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;

namespace AudioProcessorAndStreamer.Services.Vst;

public class HostCommandStub : IVstHostCommandStub, IVstHostCommands20
{
    private IVstPluginContext? _pluginContext;
    private int _sampleRate = 48000;
    private int _blockSize = 512;

    public IVstPluginContext PluginContext
    {
        get => _pluginContext!;
        set => _pluginContext = value;
    }

    public int CurrentSampleRate
    {
        get => _sampleRate;
        set => _sampleRate = value;
    }

    public int CurrentBlockSize
    {
        get => _blockSize;
        set => _blockSize = value;
    }

    IVstHostCommands20 IVstHostCommandStub.Commands => this;

    #region IVstHostCommands10

    public int GetCurrentPluginID()
    {
        return _pluginContext?.PluginInfo?.PluginID ?? 0;
    }

    public void SetParameterAutomated(int index, float value)
    {
        // Parameter automation callback
    }

    public int GetVersion()
    {
        return 2400; // VST 2.4
    }

    public string GetProductString()
    {
        return "AudioProcessorAndStreamer";
    }

    public string GetVendorString()
    {
        return "AudioProcessorAndStreamer";
    }

    public int GetVendorVersion()
    {
        return 1000;
    }

    public VstCanDoResult CanDo(string cando)
    {
        return cando switch
        {
            "sendVstEvents" => VstCanDoResult.Yes,
            "sendVstMidiEvent" => VstCanDoResult.Yes,
            "sendVstTimeInfo" => VstCanDoResult.Yes,
            "receiveVstEvents" => VstCanDoResult.Yes,
            "receiveVstMidiEvent" => VstCanDoResult.Yes,
            "sizeWindow" => VstCanDoResult.Yes,
            "startStopProcess" => VstCanDoResult.Yes,
            _ => VstCanDoResult.Unknown
        };
    }

    public int GetCurrentProcessLevel()
    {
        return (int)VstProcessLevels.Realtime;
    }

    public VstAutomationStates GetAutomationState()
    {
        return VstAutomationStates.Off;
    }

    public void ProcessIdle()
    {
        // Called when plugin wants idle time
    }

    #endregion

    #region IVstHostCommands20

    public bool ProcessEvents(VstEvent[] events)
    {
        return true;
    }

    public bool IoChanged()
    {
        return true;
    }

    public bool SizeWindow(int width, int height)
    {
        return true;
    }

    public bool BeginEdit(int index)
    {
        return true;
    }

    public bool EndEdit(int index)
    {
        return true;
    }

    public VstTimeInfo GetTimeInfo(VstTimeInfoFlags filterFlags)
    {
        return new VstTimeInfo
        {
            SamplePosition = 0,
            SampleRate = _sampleRate,
            NanoSeconds = 0,
            PpqPosition = 0,
            Tempo = 120,
            BarStartPosition = 0,
            CycleStartPosition = 0,
            CycleEndPosition = 0,
            TimeSignatureNumerator = 4,
            TimeSignatureDenominator = 4,
            SmpteOffset = 0,
            SmpteFrameRate = VstSmpteFrameRate.Smpte25fps,
            SamplesToNearestClock = 0,
            Flags = VstTimeInfoFlags.TempoValid | VstTimeInfoFlags.TimeSignatureValid
        };
    }

    public string GetDirectory()
    {
        return Environment.CurrentDirectory;
    }

    public bool UpdateDisplay()
    {
        return true;
    }

    public VstHostLanguage GetLanguage()
    {
        return VstHostLanguage.English;
    }

    public bool OpenFileSelector(VstFileSelect fileSelect)
    {
        return false;
    }

    public bool CloseFileSelector(VstFileSelect fileSelect)
    {
        return false;
    }

    public float GetSampleRate()
    {
        return _sampleRate;
    }

    public int GetBlockSize()
    {
        return _blockSize;
    }

    public int GetInputLatency()
    {
        return 0;
    }

    public int GetOutputLatency()
    {
        return 0;
    }

    public VstProcessLevels GetProcessLevel()
    {
        return VstProcessLevels.Realtime;
    }

    #endregion
}
