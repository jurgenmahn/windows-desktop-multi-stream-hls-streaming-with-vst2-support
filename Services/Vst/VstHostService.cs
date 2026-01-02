using System.IO;
using AudioProcessorAndStreamer.Infrastructure;
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
        // This wrapper method checks prerequisites before calling the actual loader
        // This avoids JIT compilation issues with Jacobi types
        DebugLogger.Log("VstHostService", $"LoadPlugin(string) called with: {pluginPath}");

        // Check all required files first
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var interopPath = Path.Combine(baseDir, "Jacobi.Vst.Host.Interop.dll");
        var corePath = Path.Combine(baseDir, "Jacobi.Vst.Core.dll");
        var ijwhostPath = Path.Combine(baseDir, "Ijwhost.dll");

        DebugLogger.Log("VstHostService", $"Base directory: {baseDir}");
        DebugLogger.Log("VstHostService", $"Jacobi.Vst.Host.Interop.dll exists: {File.Exists(interopPath)}");
        DebugLogger.Log("VstHostService", $"Jacobi.Vst.Core.dll exists: {File.Exists(corePath)}");
        DebugLogger.Log("VstHostService", $"Ijwhost.dll exists: {File.Exists(ijwhostPath)}");

        if (!File.Exists(interopPath))
            throw new FileNotFoundException("Jacobi.Vst.Host.Interop.dll not found", interopPath);
        if (!File.Exists(corePath))
            throw new FileNotFoundException("Jacobi.Vst.Core.dll not found", corePath);
        if (!File.Exists(ijwhostPath))
            throw new FileNotFoundException("Ijwhost.dll not found - required for C++/CLI assemblies", ijwhostPath);

        var resolvedPluginPath = ResolvePluginPath(pluginPath);
        DebugLogger.Log("VstHostService", $"Resolved plugin path: {resolvedPluginPath}");
        DebugLogger.Log("VstHostService", $"Plugin file exists: {File.Exists(resolvedPluginPath)}");

        if (!File.Exists(resolvedPluginPath))
            throw new FileNotFoundException($"VST plugin not found: {resolvedPluginPath}", resolvedPluginPath);

        // Now call the actual loader (in a separate method to control JIT timing)
        return LoadPluginInternal(resolvedPluginPath);
    }

    // Separate method to isolate Jacobi type references
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private VstPluginInstance? LoadPluginInternal(string resolvedPath)
    {
        try
        {
            DebugLogger.Log("VstHostService", $"LoadPluginInternal - Creating HostCommandStub...");
            var hostStub = new HostCommandStub();

            DebugLogger.Log("VstHostService", $"Creating VstPluginContext for: {resolvedPath}");
            var context = VstPluginContext.Create(resolvedPath, hostStub);
            DebugLogger.Log("VstHostService", $"VstPluginContext created successfully");

            hostStub.PluginContext = context;

            DebugLogger.Log("VstHostService", $"Opening plugin...");
            context.PluginCommandStub.Commands.Open();
            DebugLogger.Log("VstHostService", $"Plugin opened successfully");

            DebugLogger.Log("VstHostService", $"Creating VstPluginInstance...");
            var instance = new VstPluginInstance(context, hostStub, resolvedPath);
            DebugLogger.Log("VstHostService", $"VstPluginInstance created successfully");

            lock (_lock)
            {
                _loadedPlugins.Add(instance);
            }

            DebugLogger.Log("VstHostService", $"Loaded VST plugin successfully: {resolvedPath}");
            return instance;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("VstHostService", $"EXCEPTION in LoadPluginInternal: {ex.GetType().Name}: {ex.Message}");
            DebugLogger.Log("VstHostService", $"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                DebugLogger.Log("VstHostService", $"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            throw;
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
        DebugLogger.Log("VstHostService", $"LoadPlugin(config) called for: {config.PluginPath}");

        try
        {
            DebugLogger.Log("VstHostService", $"About to call LoadPlugin(string) with: {config.PluginPath}");
            var instance = LoadPlugin(config.PluginPath);
            DebugLogger.Log("VstHostService", $"LoadPlugin(string) returned: {(instance != null ? "instance" : "null")}");

            if (instance != null && config.PresetData != null)
            {
                DebugLogger.Log("VstHostService", $"Setting preset data for: {config.PluginPath}");
                instance.SetPresetData(config.PresetData);
            }

            if (instance != null)
            {
                instance.IsBypassed = config.IsBypassed;
                DebugLogger.Log("VstHostService", $"Plugin loaded, bypassed: {config.IsBypassed}");
            }

            return instance;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("VstHostService", $"EXCEPTION in LoadPlugin(config): {ex.GetType().Name}: {ex.Message}");
            DebugLogger.Log("VstHostService", $"Stack trace: {ex.StackTrace}");
            throw;
        }
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
