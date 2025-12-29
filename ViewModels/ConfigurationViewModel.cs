using System.Collections.ObjectModel;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

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
    private string _baseDomain = "http://localhost:8080";

    [ObservableProperty]
    private int _webServerPort = 8080;

    [ObservableProperty]
    private string _hlsOutputDirectory = "hls_output";

    public bool HasChanges { get; private set; }

    public ConfigurationViewModel()
    {
        _deviceEnumerator = new AudioDeviceEnumerator();
        RefreshAudioDevices();
    }

    public void LoadConfiguration(AppConfiguration config, IEnumerable<StreamConfiguration> streamConfigs)
    {
        BaseDomain = config.BaseDomain;
        WebServerPort = config.WebServerPort;
        HlsOutputDirectory = config.HlsOutputDirectory;

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
            HlsOutputDirectory = HlsOutputDirectory
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
    private void AddStream()
    {
        var newStream = new StreamConfiguration
        {
            Name = $"Stream {Streams.Count + 1}",
            StreamPath = $"stream{Streams.Count + 1}"
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
        OnPropertyChanged(nameof(SelectedStream));
    }

    [RelayCommand]
    private void AddEncodingProfile()
    {
        if (SelectedStream == null) return;

        var profile = new EncodingProfile
        {
            Name = $"Profile {SelectedStream.EncodingProfiles.Count + 1}",
            Bitrate = 128000
        };

        SelectedStream.EncodingProfiles.Add(profile);
        HasChanges = true;
        OnPropertyChanged(nameof(SelectedStream));
    }

    [RelayCommand]
    private void RemoveEncodingProfile(EncodingProfile? profile)
    {
        if (SelectedStream == null || profile == null) return;

        SelectedStream.EncodingProfiles.Remove(profile);
        HasChanges = true;
        OnPropertyChanged(nameof(SelectedStream));
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
