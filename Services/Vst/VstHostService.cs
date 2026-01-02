using System.IO;
using AudioProcessorAndStreamer.Models;
using Jacobi.Vst.Core.Host;
using Jacobi.Vst.Host.Interop;

namespace AudioProcessorAndStreamer.Services.Vst;

public class VstHostService : IVstHostService
{
    private readonly List<VstPluginInstance> _loadedPlugins = new();
    private readonly object _lock = new();

    public VstPluginInstance? LoadPlugin(string pluginPath)
    {
        // Resolve relative paths to absolute using application base directory
        var resolvedPath = ResolvePluginPath(pluginPath);

        if (!File.Exists(resolvedPath))
        {
            System.Diagnostics.Debug.WriteLine($"VST plugin not found: {resolvedPath}");
            return null;
        }

        try
        {
            var hostStub = new HostCommandStub();
            var context = VstPluginContext.Create(resolvedPath, hostStub);
            hostStub.PluginContext = context;

            context.PluginCommandStub.Commands.Open();

            var instance = new VstPluginInstance(context, hostStub, resolvedPath);

            lock (_lock)
            {
                _loadedPlugins.Add(instance);
            }

            System.Diagnostics.Debug.WriteLine($"Loaded VST plugin: {resolvedPath}");
            return instance;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load VST plugin {resolvedPath}: {ex.Message}");
            return null;
        }
    }

    private static string ResolvePluginPath(string pluginPath)
    {
        // If already absolute, use as-is
        if (Path.IsPathRooted(pluginPath))
        {
            return pluginPath;
        }

        // Resolve relative to application base directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, pluginPath);
    }

    public VstPluginInstance? LoadPlugin(VstPluginConfig config)
    {
        var instance = LoadPlugin(config.PluginPath);

        if (instance != null && config.PresetData != null)
        {
            instance.SetPresetData(config.PresetData);
        }

        if (instance != null)
        {
            instance.IsBypassed = config.IsBypassed;
        }

        return instance;
    }

    public void UnloadPlugin(VstPluginInstance plugin)
    {
        lock (_lock)
        {
            _loadedPlugins.Remove(plugin);
        }

        plugin.Dispose();
    }

    public IEnumerable<string> ScanForPlugins(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Enumerable.Empty<string>();
        }

        var validPlugins = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            var hostStub = new HostCommandStub();
            IVstPluginContext? context = null;

            try
            {
                context = VstPluginContext.Create(file, hostStub);
                validPlugins.Add(file);
            }
            catch
            {
                // Not a valid VST plugin
            }
            // Note: IVstPluginContext from VstPluginContext.Create may not be directly disposable
            // The unmanaged resources are managed by the interop layer
        }

        return validPlugins;
    }
}
