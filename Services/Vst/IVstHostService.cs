using AudioProcessorAndStreamer.Models;

namespace AudioProcessorAndStreamer.Services.Vst;

public interface IVstHostService
{
    VstPluginInstance? LoadPlugin(string pluginPath);
    VstPluginInstance? LoadPlugin(VstPluginConfig config);
    void UnloadPlugin(VstPluginInstance plugin);
    IEnumerable<string> ScanForPlugins(string directory);
}
