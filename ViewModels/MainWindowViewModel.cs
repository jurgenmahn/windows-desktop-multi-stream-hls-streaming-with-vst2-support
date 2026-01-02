using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AudioProcessorAndStreamer.Views;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services;
using AudioProcessorAndStreamer.Services.Audio;
using AudioProcessorAndStreamer.Services.Streaming;
using AudioProcessorAndStreamer.Services.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AudioProcessorAndStreamer.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IStreamManager _streamManager;
    private readonly HlsWebServer _webServer;
    private readonly IMonitorOutputService _monitorService;
    private readonly AppConfiguration _config;
    private readonly AutoUpdateService _autoUpdateService;
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

    [ObservableProperty]
    private string _listenerStatus = string.Empty;

    public MainWindowViewModel(
        IStreamManager streamManager,
        HlsWebServer webServer,
        IMonitorOutputService monitorService,
        IOptions<AppConfiguration> config)
    {
        _streamManager = streamManager;
        _webServer = webServer;
        _monitorService = monitorService;
        _config = config.Value;
        _autoUpdateService = new AutoUpdateService();

        // Use LocalApplicationData for user config files (writable, per-user)
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioProcessorAndStreamer");
        Directory.CreateDirectory(appDataDir);

        _configPath = Path.Combine(appDataDir, "streams.json");
        _appConfigPath = Path.Combine(appDataDir, "appconfig.json");

        _webServer.RequestReceived += (s, msg) =>
            System.Diagnostics.Debug.WriteLine($"[HLS] {msg}");

        _webServer.ListenerCountChanged += OnListenerCountChanged;

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
                    _config.DebugAudioEnabled = savedConfig.DebugAudioEnabled;
                    _config.MonitorOutputDevice = savedConfig.MonitorOutputDevice;
                    _config.HlsSegmentDuration = savedConfig.HlsSegmentDuration;
                    _config.HlsPlaylistSize = savedConfig.HlsPlaylistSize;
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
                        var vm = new StreamViewModel(config, _streamManager, _monitorService);
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
            var vm = new StreamViewModel(config, _streamManager, _monitorService);
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

    private void OnListenerCountChanged(object? sender, EventArgs e)
    {
        // Update listener status on UI thread
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var counts = _webServer.GetAllListenerCounts();
            if (counts.Count == 0)
            {
                ListenerStatus = string.Empty;
            }
            else
            {
                // Build status string showing streams with listeners
                var parts = new List<string>();
                foreach (var kvp in counts)
                {
                    // Find stream name from path
                    var stream = Streams.FirstOrDefault(s => s.Configuration.StreamPath == kvp.Key);
                    var name = stream?.Name ?? kvp.Key;
                    parts.Add($"{name}: {kvp.Value}");
                }
                ListenerStatus = string.Join(" | ", parts);
            }
        });
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        DebugLogger.Log("MainWindowViewModel", $"StartServerAsync called - IsServerRunning: {IsServerRunning}");

        if (IsServerRunning)
        {
            DebugLogger.Log("MainWindowViewModel", "Server already running, returning");
            return;
        }

        try
        {
            DebugLogger.Log("MainWindowViewModel", $"Starting web server - config port: {_config.WebServerPort}");
            await _webServer.StartAsync();
            IsServerRunning = true;
            ServerUrl = $"{_webServer.BaseUrl}{_config.StreamsPagePath}";
            ServerStatus = $"Server running on port {_webServer.Port}";
            DebugLogger.Log("MainWindowViewModel", $"Server started - ServerStatus: {ServerStatus}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log("MainWindowViewModel", $"Server start failed: {ex.Message}");
            ServerStatus = $"Server failed: {ex.Message}";
            MessageBox.Show($"Failed to start web server: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        DebugLogger.Log("MainWindowViewModel", $"StopServerAsync called - IsServerRunning: {IsServerRunning}");

        if (!IsServerRunning)
        {
            DebugLogger.Log("MainWindowViewModel", "Server not running, returning");
            return;
        }

        await _webServer.StopAsync();
        IsServerRunning = false;
        ServerUrl = string.Empty;
        ServerStatus = "Server stopped";
        DebugLogger.Log("MainWindowViewModel", "Server stopped successfully");
    }

    [RelayCommand]
    private void OpenServerUrl()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ServerUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open URL: {ex.Message}");
        }
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
                new EncodingProfile { Name = "192kbps AAC", Codec = AudioCodec.Aac, Bitrate = 192000 },
                new EncodingProfile { Name = "256kbps AAC", Codec = AudioCodec.Aac, Bitrate = 256000 }
            }
        };

        var vm = new StreamViewModel(config, _streamManager, _monitorService);
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
                DebugLogger.Log("MainWindowViewModel", $"Updating config - old port: {_config.WebServerPort}, new port: {dialog.ResultConfiguration.WebServerPort}");
                _config.BaseDomain = dialog.ResultConfiguration.BaseDomain;
                _config.WebServerPort = dialog.ResultConfiguration.WebServerPort;
                _config.HlsOutputDirectory = dialog.ResultConfiguration.HlsOutputDirectory;
                _config.LazyProcessing = dialog.ResultConfiguration.LazyProcessing;
                _config.StreamsPagePath = dialog.ResultConfiguration.StreamsPagePath;
                _config.DebugAudioEnabled = dialog.ResultConfiguration.DebugAudioEnabled;
                _config.MonitorOutputDevice = dialog.ResultConfiguration.MonitorOutputDevice;
                _config.HlsSegmentDuration = dialog.ResultConfiguration.HlsSegmentDuration;
                _config.HlsPlaylistSize = dialog.ResultConfiguration.HlsPlaylistSize;
                DebugLogger.Log("MainWindowViewModel", $"Config updated - _config.WebServerPort is now: {_config.WebServerPort}");
                SaveAppConfig();

                // Update monitor output device immediately
                _monitorService.SetOutputDevice(_config.MonitorOutputDevice);
            }

            // Prepare all streams for reload (show "Reloading..." status)
            foreach (var stream in Streams)
            {
                stream.PrepareForReload();
            }

            // Small delay to let UI update
            await Task.Delay(50);

            // Stop all streams on background thread to avoid UI freeze
            await Task.Run(() => _streamManager.StopAllStreams());
            AreAllStreamsRunning = false;

            // Dispose old streams on background thread
            var oldStreams = Streams.ToList();
            await Task.Run(() =>
            {
                foreach (var stream in oldStreams)
                {
                    stream.Dispose();
                }
            });
            Streams.Clear();

            // Add new streams
            foreach (var config in dialog.ResultStreams)
            {
                var vm = new StreamViewModel(config, _streamManager, _monitorService);

                // Mark streams that need to be restarted
                if (runningStreamIds.Contains(config.Id))
                {
                    vm.SetWaitingForRestart();
                }

                Streams.Add(vm);
            }

            // Let UI update after adding streams
            await Task.Delay(50);

            SaveStreamsToConfig();
            UpdateWebServerStreams();

            // Restart server if it was running (stop first to apply new port/config)
            if (wasServerRunning)
            {
                DebugLogger.Log("MainWindowViewModel", $"Restarting server - config port: {_config.WebServerPort}");
                await StopServerAsync();
                DebugLogger.Log("MainWindowViewModel", "Server stopped, now starting with new config...");
                await StartServerAsync();
                DebugLogger.Log("MainWindowViewModel", $"Server restarted - ServerStatus: {ServerStatus}");
            }

            // Restart streams one by one with delays
            var streamsToRestart = Streams.Where(s => runningStreamIds.Contains(s.Id)).ToList();
            foreach (var stream in streamsToRestart)
            {
                await stream.CompleteReloadAsync(shouldRestart: true);

                // Wait between stream restarts to prevent overwhelming the system
                await Task.Delay(300);
            }

            // Complete reload for streams that don't need to restart
            foreach (var stream in Streams.Where(s => !runningStreamIds.Contains(s.Id)))
            {
                await stream.CompleteReloadAsync(shouldRestart: false);
            }
        }
    }

    [RelayCommand]
    private void OpenAbout()
    {
        var dialog = new Views.AboutDialog();
        dialog.Owner = Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Configuration",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var exportData = JsonSerializer.Deserialize<ConfigExportData>(json);

            if (exportData == null)
            {
                MessageBox.Show("Invalid configuration file.", "Import Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show(
                "Importing will replace your current configuration.\n\nDo you want to continue?",
                "Import Configuration",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // Stop all streams and server
            var wasServerRunning = IsServerRunning;
            if (wasServerRunning)
            {
                await StopServerAsync();
            }
            _streamManager.StopAllStreams();

            // Dispose old streams
            foreach (var stream in Streams)
            {
                stream.Dispose();
            }
            Streams.Clear();

            // Apply app configuration
            if (exportData.AppConfig != null)
            {
                _config.BaseDomain = exportData.AppConfig.BaseDomain;
                _config.WebServerPort = exportData.AppConfig.WebServerPort;
                _config.HlsOutputDirectory = exportData.AppConfig.HlsOutputDirectory;
                _config.LazyProcessing = exportData.AppConfig.LazyProcessing;
                _config.StreamsPagePath = exportData.AppConfig.StreamsPagePath;
                _config.DebugAudioEnabled = exportData.AppConfig.DebugAudioEnabled;
                _config.MonitorOutputDevice = exportData.AppConfig.MonitorOutputDevice;
                SaveAppConfig();
                _monitorService.SetOutputDevice(_config.MonitorOutputDevice);
            }

            // Apply stream configurations
            if (exportData.Streams != null)
            {
                foreach (var config in exportData.Streams)
                {
                    var vm = new StreamViewModel(config, _streamManager, _monitorService);
                    Streams.Add(vm);
                }
                SaveStreamsToConfig();
                UpdateWebServerStreams();
            }

            // Restart server if it was running (it was stopped above)
            if (wasServerRunning)
            {
                await StartServerAsync();
            }

            MessageBox.Show("Configuration imported successfully.", "Import Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import configuration: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Configuration",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "AudioProcessorConfig.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var exportData = new ConfigExportData
            {
                AppConfig = new AppConfiguration
                {
                    BaseDomain = _config.BaseDomain,
                    WebServerPort = _config.WebServerPort,
                    HlsOutputDirectory = _config.HlsOutputDirectory,
                    LazyProcessing = _config.LazyProcessing,
                    StreamsPagePath = _config.StreamsPagePath,
                    DebugAudioEnabled = _config.DebugAudioEnabled,
                    MonitorOutputDevice = _config.MonitorOutputDevice
                },
                Streams = Streams.Select(s => s.Configuration).ToList()
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dialog.FileName, json);

            MessageBox.Show("Configuration exported successfully.", "Export Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export configuration: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        DebugLogger.Log("MainWindowViewModel", "Checking for updates...");

        var updateInfo = await _autoUpdateService.CheckForUpdateAsync();

        if (updateInfo == null)
        {
            MessageBox.Show(
                $"You are running the latest version ({_autoUpdateService.CurrentVersion}).",
                "No Updates Available",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Build the message with optional release notes
        var message = $"A new version is available!\n\n" +
                      $"Current version: {_autoUpdateService.CurrentVersion}\n" +
                      $"New version: {updateInfo.Version}\n";

        if (!string.IsNullOrEmpty(updateInfo.ReleaseNotes))
        {
            message += $"\nRelease notes:\n{updateInfo.ReleaseNotes}\n";
        }

        message += "\nWould you like to download and install the update now?";

        var result = MessageBox.Show(
            message,
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        // Download the update
        var downloadPath = await _autoUpdateService.DownloadUpdateAsync(updateInfo);

        if (downloadPath == null)
        {
            MessageBox.Show(
                "Failed to download the update. Please try again later or download manually.",
                "Download Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // Ask to run the installer
        var runResult = MessageBox.Show(
            "Update downloaded successfully!\n\nThe application will close and the installer will start.\n\nContinue?",
            "Install Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (runResult != MessageBoxResult.Yes)
            return;

        // Launch installer and exit
        if (_autoUpdateService.LaunchInstaller(downloadPath))
        {
            Application.Current.Shutdown();
        }
        else
        {
            MessageBox.Show(
                $"Failed to start the installer.\n\nYou can run it manually from:\n{downloadPath}",
                "Installer Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Checks for updates silently on startup - only notifies if update is available.
    /// </summary>
    public async Task CheckForUpdatesSilentAsync()
    {
        DebugLogger.Log("MainWindowViewModel", "Silent update check on startup...");

        var updateInfo = await _autoUpdateService.CheckForUpdateAsync();

        if (updateInfo == null)
            return;

        // Build the message with optional release notes
        var message = $"A new version ({updateInfo.Version}) is available!\n\n" +
                      $"Current version: {_autoUpdateService.CurrentVersion}\n";

        if (!string.IsNullOrEmpty(updateInfo.ReleaseNotes))
        {
            message += $"\nRelease notes:\n{updateInfo.ReleaseNotes}\n";
        }

        message += "\nWould you like to download and install the update now?";

        var result = MessageBox.Show(
            message,
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        // Download and install
        var downloadPath = await _autoUpdateService.DownloadUpdateAsync(updateInfo);

        if (downloadPath == null)
        {
            MessageBox.Show(
                "Failed to download the update. Please try again later.",
                "Download Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var runResult = MessageBox.Show(
            "Update downloaded successfully!\n\nThe application will close and the installer will start.\n\nContinue?",
            "Install Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (runResult != MessageBoxResult.Yes)
            return;

        if (_autoUpdateService.LaunchInstaller(downloadPath))
        {
            Application.Current.Shutdown();
        }
    }

    [RelayCommand]
    private void Exit()
    {
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _webServer.ListenerCountChanged -= OnListenerCountChanged;

        StopAllStreams();

        foreach (var stream in Streams)
        {
            stream.Dispose();
        }
    }
}
