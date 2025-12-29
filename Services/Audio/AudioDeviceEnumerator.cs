using AudioProcessorAndStreamer.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioProcessorAndStreamer.Services.Audio;

public class AudioDeviceEnumerator
{
    public IEnumerable<AudioDeviceInfo> GetWasapiDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        // Capture devices (microphones, line-in)
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            yield return new AudioDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName,
                DriverType = AudioDriverType.Wasapi,
                Channels = device.AudioClient.MixFormat.Channels,
                SampleRate = device.AudioClient.MixFormat.SampleRate
            };
        }

        // Render devices (for loopback capture)
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            yield return new AudioDeviceInfo
            {
                Id = device.ID,
                Name = $"{device.FriendlyName} (Loopback)",
                DriverType = AudioDriverType.Wasapi,
                Channels = device.AudioClient.MixFormat.Channels,
                SampleRate = device.AudioClient.MixFormat.SampleRate
            };
        }
    }

    public IEnumerable<AudioDeviceInfo> GetAsioDevices()
    {
        var driverNames = AsioOut.GetDriverNames();

        foreach (var driverName in driverNames)
        {
            yield return new AudioDeviceInfo
            {
                Id = driverName,
                Name = driverName,
                DriverType = AudioDriverType.Asio,
                Channels = 2, // ASIO channel count requires opening the driver
                SampleRate = 48000 // Default, actual rate set during init
            };
        }
    }

    public IEnumerable<AudioDeviceInfo> GetAllDevices()
    {
        foreach (var device in GetWasapiDevices())
        {
            yield return device;
        }

        foreach (var device in GetAsioDevices())
        {
            yield return device;
        }
    }

    public AudioDeviceInfo? GetDefaultCaptureDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

            return new AudioDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName,
                DriverType = AudioDriverType.Wasapi,
                Channels = device.AudioClient.MixFormat.Channels,
                SampleRate = device.AudioClient.MixFormat.SampleRate
            };
        }
        catch
        {
            return null;
        }
    }
}
