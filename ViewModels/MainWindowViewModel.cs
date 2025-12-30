using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AudioProcessorAndStreamer.Views;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Streaming;
using AudioProcessorAndStreamer.Services.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IStreamManager _streamManager;
    private readonly HlsWebServer _webServer;
    private readonly AppConfiguration _config;
    private readonly string _configPath;
    private readonly string _appConfigPath;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<StreamViewModel> _streams = new();

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _serverStatus = "Server stopped";

    [ObservableProperty]
    private bool _areAllStreamsRunning;

    public MainWindowViewModel(
        IStreamManager streamManager,
        HlsWebServer webServer,
        IOptions<AppConfiguration> config)
    {
        _streamManager = streamManager;
        _webServer = webServer;
        _config = config.Value;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "streams.json");
        _appConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appconfig.json");

        _webServer.RequestReceived += (s, msg) =>
            System.Diagnostics.Debug.WriteLine($"[HLS] {msg}");

        LoadAppConfig();
        LoadStreamsFromConfig();
        UpdateWebServerStreams();
    }

    private void LoadAppConfig()
    {
        if (File.Exists(_appConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_appConfigPath);
                var savedConfig = JsonSerializer.Deserialize<AppConfiguration>(json);
                if (savedConfig != null)
                {
                    _config.BaseDomain = savedConfig.BaseDomain;
                    _config.WebServerPort = savedConfig.WebServerPort;
                    _config.HlsOutputDirectory = savedConfig.HlsOutputDirectory;
                    _config.LazyProcessing = savedConfig.LazyProcessing;
                    _config.StreamsPagePath = savedConfig.StreamsPagePath;
                    _config.VstOutputBufferSeconds = savedConfig.VstOutputBufferSeconds;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load appconfig.json: {ex.Message}");
            }
        }
    }

    private void SaveAppConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_appConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save appconfig.json: {ex.Message}");
        }
    }

    private void LoadStreamsFromConfig()
    {
        // Try loading from streams.json first
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var streamConfigs = JsonSerializer.Deserialize<List<StreamConfiguration>>(json);

                if (streamConfigs != null)
                {
                    foreach (var config in streamConfigs)
                    {
                        var vm = new StreamViewModel(config, _streamManager);
                        Streams.Add(vm);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load streams.json: {ex.Message}");
            }
        }

        // Fall back to appsettings.json config
        foreach (var config in _config.Streams)
        {
            var vm = new StreamViewModel(config, _streamManager);
            Streams.Add(vm);
        }
    }

    public void SaveStreamsToConfig()
    {
        try
        {
            var configs = Streams.Select(s => s.Configuration).ToList();
            var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save streams.json: {ex.Message}");
        }
    }

    private void UpdateWebServerStreams()
    {
        var configs = Streams.Select(s => s.Configuration).ToList();
        _webServer.UpdateStreams(configs);
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (IsServerRunning) return;

        try
        {
            await _webServer.StartAsync();
            IsServerRunning = true;
            ServerUrl = $"{_webServer.BaseUrl}/hls/";
            ServerStatus = $"Server running on port {_webServer.Port}";
        }
        catch (Exception ex)
        {
            ServerStatus = $"Server failed: {ex.Message}";
            MessageBox.Show($"Failed to start web server: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        if (!IsServerRunning) return;

        await _webServer.StopAsync();
        IsServerRunning = false;
        ServerUrl = string.Empty;
        ServerStatus = "Server stopped";
    }

    [RelayCommand]
    private void StartAllStreams()
    {
        foreach (var stream in Streams)
        {
            stream.StartCommand.Execute(null);
        }
        AreAllStreamsRunning = true;
    }

    [RelayCommand]
    private void StopAllStreams()
    {
        _streamManager.StopAllStreams();
        AreAllStreamsRunning = false;
    }

    [RelayCommand]
    private void AddStream()
    {
        var config = new StreamConfiguration
        {
            Name = $"Stream {Streams.Count + 1}",
            StreamPath = $"stream{Streams.Count + 1}",
            VstPlugins = new List<VstPluginConfig>
            {
                new VstPluginConfig
                {
                    PluginPath = "Plugins/vst_stereo_tool_64.dll",
                    PluginName = "Stereo Tool",
                    Order = 0
                }
            },
            EncodingProfiles = new List<EncodingProfile>
            {
                new EncodingProfile { Name = "64kbps AAC", Codec = AudioCodec.Aac, Bitrate = 64000 },
                new EncodingProfile { Name = "128kbps AAC", Codec = AudioCodec.Aac, Bitrate = 128000 },
                new EncodingProfile { Name = "192kbps AAC", Codec = AudioCodec.Aac, Bitrate = 192000 }
            }
        };

        var vm = new StreamViewModel(config, _streamManager);
        Streams.Add(vm);
        SaveStreamsToConfig();
        UpdateWebServerStreams();
    }

    [RelayCommand]
    private void RemoveStream(StreamViewModel? stream)
    {
        if (stream == null) return;

        stream.Stop();
        stream.Dispose();
        Streams.Remove(stream);
        SaveStreamsToConfig();
        UpdateWebServerStreams();
    }

    [RelayCommand]
    private async Task OpenConfigurationAsync()
    {
        var dialog = new Views.ConfigurationDialog();
        dialog.Owner = Application.Current.MainWindow;
        dialog.LoadConfiguration(_config, Streams.Select(s => s.Configuration));

        if (dialog.ShowDialog() == true && dialog.ResultStreams != null)
        {
            // Remember running state before stopping
            var wasServerRunning = IsServerRunning;
            var runningStreamIds = Streams.Where(s => s.IsRunning).Select(s => s.Id).ToHashSet();

            // Update app config
            if (dialog.ResultConfiguration != null)
            {
                _config.BaseDomain = dialog.ResultConfiguration.BaseDomain;
                _config.WebServerPort = dialog.ResultConfiguration.WebServerPort;
                _config.HlsOutputDirectory = dialog.ResultConfiguration.HlsOutputDirectory;
                _config.LazyProcessing = dialog.ResultConfiguration.LazyProcessing;
                _config.StreamsPagePath = dialog.ResultConfiguration.StreamsPagePath;
                _config.VstOutputBufferSeconds = dialog.ResultConfiguration.VstOutputBufferSeconds;
                SaveAppConfig();
            }

            // Stop all streams before updating
            StopAllStreams();

            // Rebuild stream list
            foreach (var stream in Streams)
            {
                stream.Dispose();
            }
            Streams.Clear();

            foreach (var config in dialog.ResultStreams)
            {
                var vm = new StreamViewModel(config, _streamManager);
                Streams.Add(vm);
            }

            SaveStreamsToConfig();
            UpdateWebServerStreams();

            // Restore running state - restart server if it was running
            if (wasServerRunning)
            {
                await StartServerAsync();
            }

            // Restart streams that were previously running (by matching IDs)
            foreach (var stream in Streams.Where(s => runningStreamIds.Contains(s.Id)))
            {
                stream.StartCommand.Execute(null);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAllStreams();

        foreach (var stream in Streams)
        {
            stream.Dispose();
        }
    }
}
