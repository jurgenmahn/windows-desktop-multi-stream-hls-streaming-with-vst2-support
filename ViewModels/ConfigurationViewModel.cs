using System.Collections.ObjectModel;
using System.IO;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AudioProcessorAndStreamer.ViewModels;

public partial class ConfigurationViewModel : ObservableObject
{
    private readonly AudioDeviceEnumerator _deviceEnumerator;

    [ObservableProperty]
    private ObservableCollection<StreamConfiguration> _streams = new();

    [ObservableProperty]
    private StreamConfiguration? _selectedStream;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioDevices = new();

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private ObservableCollection<string> _outputDevices = new();

    [ObservableProperty]
    private string _monitorOutputDevice = "";

    [ObservableProperty]
    private string _baseDomain = "http://localhost:8080";

    [ObservableProperty]
    private int _webServerPort = 8080;

    [ObservableProperty]
    private string _hlsOutputDirectory = "stream_output";

    [ObservableProperty]
    private bool _lazyProcessing = true;

    [ObservableProperty]
    private string _streamsPagePath = "/streams";

    [ObservableProperty]
    private bool _debugAudioEnabled = false;

    [ObservableProperty]
    private int _hlsSegmentDuration = 4;

    [ObservableProperty]
    private int _hlsPlaylistSize = 5;

    public bool HasChanges { get; private set; }

    public ConfigurationViewModel()
    {
        _deviceEnumerator = new AudioDeviceEnumerator();
        RefreshAudioDevices();
        RefreshOutputDevices();
    }

    public void LoadConfiguration(AppConfiguration config, IEnumerable<StreamConfiguration> streamConfigs)
    {
        BaseDomain = config.BaseDomain;
        WebServerPort = config.WebServerPort;
        HlsOutputDirectory = config.HlsOutputDirectory;
        LazyProcessing = config.LazyProcessing;
        StreamsPagePath = config.StreamsPagePath;
        DebugAudioEnabled = config.DebugAudioEnabled;
        MonitorOutputDevice = config.MonitorOutputDevice;
        HlsSegmentDuration = config.HlsSegmentDuration;
        HlsPlaylistSize = config.HlsPlaylistSize;

        Streams.Clear();
        foreach (var stream in streamConfigs)
        {
            Streams.Add(CloneStreamConfiguration(stream));
        }

        if (Streams.Count > 0)
        {
            SelectedStream = Streams[0];
        }

        HasChanges = false;
    }

    public (AppConfiguration config, List<StreamConfiguration> streams) GetConfiguration()
    {
        var config = new AppConfiguration
        {
            BaseDomain = BaseDomain,
            WebServerPort = WebServerPort,
            HlsOutputDirectory = HlsOutputDirectory,
            LazyProcessing = LazyProcessing,
            StreamsPagePath = StreamsPagePath,
            DebugAudioEnabled = DebugAudioEnabled,
            MonitorOutputDevice = MonitorOutputDevice,
            HlsSegmentDuration = HlsSegmentDuration,
            HlsPlaylistSize = HlsPlaylistSize
        };

        return (config, Streams.ToList());
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        AudioDevices.Clear();
        foreach (var device in _deviceEnumerator.GetAllDevices())
        {
            AudioDevices.Add(device);
        }
    }

    [RelayCommand]
    private void RefreshOutputDevices()
    {
        var currentSelection = MonitorOutputDevice;
        OutputDevices.Clear();

        // Add empty option for "Default Device"
        OutputDevices.Add("");

        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                OutputDevices.Add(device.FriendlyName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to enumerate output devices: {ex.Message}");
        }

        // Restore selection if it still exists
        if (OutputDevices.Contains(currentSelection))
        {
            MonitorOutputDevice = currentSelection;
        }
    }

    [RelayCommand]
    private void AddStream()
    {
        // Find first preset file in Presets folder
        var presetsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets");
        string? defaultPreset = null;
        if (Directory.Exists(presetsFolder))
        {
            defaultPreset = Directory.GetFiles(presetsFolder, "*.sts")
                .OrderBy(f => f)
                .FirstOrDefault();
            // Store as relative path for portability
            if (defaultPreset != null)
            {
                defaultPreset = Path.Combine("Presets", Path.GetFileName(defaultPreset));
            }
        }

        var newStream = new StreamConfiguration
        {
            Name = $"Stream {Streams.Count + 1}",
            StreamPath = $"stream{Streams.Count + 1}",
            VstPlugins = new List<VstPluginConfig>
            {
                new VstPluginConfig
                {
                    PluginPath = "Plugins/vst_stereo_tool_64.dll",
                    PluginName = "Stereo Tool",
                    Order = 0,
                    PresetFilePath = defaultPreset
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

        Streams.Add(newStream);
        SelectedStream = newStream;
        HasChanges = true;
    }

    [RelayCommand]
    private void RemoveStream()
    {
        if (SelectedStream == null) return;

        var index = Streams.IndexOf(SelectedStream);
        Streams.Remove(SelectedStream);

        if (Streams.Count > 0)
        {
            SelectedStream = Streams[Math.Min(index, Streams.Count - 1)];
        }
        else
        {
            SelectedStream = null;
        }

        HasChanges = true;
    }

    [RelayCommand]
    private void BrowseVstPlugin()
    {
        if (SelectedStream == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select VST Plugin",
            Filter = "VST Plugins (*.dll)|*.dll|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            var vstConfig = new VstPluginConfig
            {
                PluginPath = dialog.FileName,
                PluginName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
                Order = SelectedStream.VstPlugins.Count
            };

            SelectedStream.VstPlugins.Add(vstConfig);
            HasChanges = true;
            OnPropertyChanged(nameof(SelectedStream));
        }
    }

    [RelayCommand]
    private void RemoveVstPlugin(VstPluginConfig? plugin)
    {
        if (SelectedStream == null || plugin == null) return;

        SelectedStream.VstPlugins.Remove(plugin);
        HasChanges = true;

        // Force UI refresh by temporarily changing SelectedStream
        var current = SelectedStream;
        SelectedStream = null;
        SelectedStream = current;
    }

    [RelayCommand]
    private void BrowseVstPreset(VstPluginConfig? plugin)
    {
        if (plugin == null) return;

        // Default to Presets folder
        var presetsFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets");

        var dialog = new OpenFileDialog
        {
            Title = "Select VST Preset File",
            Filter = "Stereo Tool Presets (*.sts)|*.sts|VST Presets (*.fxp;*.fxb)|*.fxp;*.fxb|All Files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = System.IO.Directory.Exists(presetsFolder) ? presetsFolder : AppDomain.CurrentDomain.BaseDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            plugin.PresetFilePath = dialog.FileName;
            HasChanges = true;
            OnPropertyChanged(nameof(SelectedStream));
        }
    }

    [RelayCommand]
    private void ClearVstPreset(VstPluginConfig? plugin)
    {
        if (plugin == null) return;

        plugin.PresetFilePath = null;
        HasChanges = true;
        OnPropertyChanged(nameof(SelectedStream));
    }

    [RelayCommand]
    private void AddEncodingProfile()
    {
        if (SelectedStream == null) return;

        var profile = new EncodingProfile
        {
            Name = $"Profile {SelectedStream.EncodingProfiles.Count + 1}",
            Codec = AudioCodec.Aac,
            Bitrate = 128000
        };

        SelectedStream.EncodingProfiles.Add(profile);
        HasChanges = true;

        // Force UI refresh by temporarily changing SelectedStream
        var current = SelectedStream;
        SelectedStream = null;
        SelectedStream = current;
    }

    [RelayCommand]
    private void RemoveEncodingProfile(EncodingProfile? profile)
    {
        if (SelectedStream == null || profile == null) return;

        SelectedStream.EncodingProfiles.Remove(profile);
        HasChanges = true;

        // Force UI refresh by temporarily changing SelectedStream
        var current = SelectedStream;
        SelectedStream = null;
        SelectedStream = current;
    }

    [RelayCommand]
    private void BrowseLogo()
    {
        if (SelectedStream == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Logo Image",
            Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedStream.LogoPath = dialog.FileName;
            HasChanges = true;
            OnPropertyChanged(nameof(SelectedStream));
        }
    }

    [RelayCommand]
    private void BrowseHlsOutputDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select HLS Output Directory",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        // Set initial directory if current value exists
        if (!string.IsNullOrEmpty(HlsOutputDirectory))
        {
            var fullPath = ResolveHlsOutputDirectory(HlsOutputDirectory);
            if (System.IO.Directory.Exists(fullPath))
            {
                dialog.SelectedPath = fullPath;
            }
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            HlsOutputDirectory = dialog.SelectedPath;
            HasChanges = true;
        }
    }

    private static string ResolveHlsOutputDirectory(string hlsOutputDirectory)
    {
        // If absolute path, use as-is
        if (System.IO.Path.IsPathRooted(hlsOutputDirectory))
        {
            return hlsOutputDirectory;
        }

        // For relative paths, use LocalApplicationData (writable location)
        var appDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioProcessorAndStreamer");
        return System.IO.Path.Combine(appDataDir, hlsOutputDirectory);
    }

    partial void OnSelectedStreamChanged(StreamConfiguration? value)
    {
        if (value != null)
        {
            var device = AudioDevices.FirstOrDefault(d =>
                d.Id == value.AudioInput.DeviceId ||
                d.Name == value.AudioInput.DeviceName);

            SelectedAudioDevice = device;
        }
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        if (SelectedStream != null && value != null)
        {
            SelectedStream.AudioInput.DeviceId = value.Id;
            SelectedStream.AudioInput.DeviceName = value.Name;
            SelectedStream.AudioInput.DriverType = value.DriverType;
            HasChanges = true;
        }
    }

    private static StreamConfiguration CloneStreamConfiguration(StreamConfiguration source)
    {
        return new StreamConfiguration
        {
            Id = source.Id,
            Name = source.Name,
            StreamPath = source.StreamPath,
            LogoPath = source.LogoPath,
            IsEnabled = source.IsEnabled,
            StreamFormat = source.StreamFormat,
            ContainerFormat = source.ContainerFormat,
            AudioInput = new AudioInputConfig
            {
                DriverType = source.AudioInput.DriverType,
                DeviceId = source.AudioInput.DeviceId,
                DeviceName = source.AudioInput.DeviceName,
                SampleRate = source.AudioInput.SampleRate,
                Channels = source.AudioInput.Channels,
                BufferSize = source.AudioInput.BufferSize
            },
            VstPlugins = source.VstPlugins.Select(v => new VstPluginConfig
            {
                PluginPath = v.PluginPath,
                PluginName = v.PluginName,
                Order = v.Order,
                IsBypassed = v.IsBypassed,
                PresetFilePath = v.PresetFilePath,
                PresetData = v.PresetData
            }).ToList(),
            EncodingProfiles = source.EncodingProfiles.Select(p => new EncodingProfile
            {
                Name = p.Name,
                Codec = p.Codec,
                Bitrate = p.Bitrate,
                SampleRate = p.SampleRate,
                SegmentDuration = p.SegmentDuration,
                PlaylistSize = p.PlaylistSize
            }).ToList()
        };
    }
}
