using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AudioProcessorAndStreamer.Views;
using System.Windows;
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

        _webServer.RequestReceived += (s, msg) =>
            System.Diagnostics.Debug.WriteLine($"[HLS] {msg}");

        LoadStreamsFromConfig();
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
            StreamPath = $"stream{Streams.Count + 1}"
        };

        var vm = new StreamViewModel(config, _streamManager);
        Streams.Add(vm);
        SaveStreamsToConfig();
    }

    [RelayCommand]
    private void RemoveStream(StreamViewModel? stream)
    {
        if (stream == null) return;

        stream.Stop();
        stream.Dispose();
        Streams.Remove(stream);
        SaveStreamsToConfig();
    }

    [RelayCommand]
    private void OpenConfiguration()
    {
        var dialog = new Views.ConfigurationDialog();
        dialog.Owner = Application.Current.MainWindow;
        dialog.LoadConfiguration(_config, Streams.Select(s => s.Configuration));

        if (dialog.ShowDialog() == true && dialog.ResultStreams != null)
        {
            // Update app config
            if (dialog.ResultConfiguration != null)
            {
                _config.BaseDomain = dialog.ResultConfiguration.BaseDomain;
                _config.WebServerPort = dialog.ResultConfiguration.WebServerPort;
                _config.HlsOutputDirectory = dialog.ResultConfiguration.HlsOutputDirectory;
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
