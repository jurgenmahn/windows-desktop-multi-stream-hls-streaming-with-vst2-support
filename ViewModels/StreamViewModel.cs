using System.Windows;
using AudioProcessorAndStreamer.Controls;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Streaming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioProcessorAndStreamer.ViewModels;

public partial class StreamViewModel : ObservableObject, IDisposable
{
    private readonly StreamConfiguration _config;
    private readonly IStreamManager _streamManager;
    private AudioStreamProcessor? _processor;
    private OscilloscopeControl? _inputOscilloscope;
    private OscilloscopeControl? _outputOscilloscope;
    private bool _disposed;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Stopped";

    public string Id => _config.Id;
    public StreamConfiguration Configuration => _config;

    public StreamViewModel(StreamConfiguration config, IStreamManager streamManager)
    {
        _config = config;
        _streamManager = streamManager;
        _name = config.Name;
    }

    public void AttachOscilloscopes(OscilloscopeControl inputScope, OscilloscopeControl outputScope)
    {
        _inputOscilloscope = inputScope;
        _outputOscilloscope = outputScope;
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning) return;

        _processor = _streamManager.StartStream(_config);

        if (_processor != null)
        {
            _processor.InputSamplesAvailable += OnInputSamplesAvailable;
            _processor.OutputSamplesAvailable += OnOutputSamplesAvailable;
            _processor.EncoderMessage += OnEncoderMessage;
            _processor.Stopped += OnProcessorStopped;

            IsRunning = true;
            StatusMessage = "Streaming";
        }
        else
        {
            StatusMessage = "Failed to start";
        }
    }

    [RelayCommand]
    public void Stop()
    {
        if (!IsRunning) return;

        _streamManager.StopStream(_config.Id);
    }

    private void OnInputSamplesAvailable(object? sender, float[] samples)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _inputOscilloscope?.UpdateSamples(samples);
        });
    }

    private void OnOutputSamplesAvailable(object? sender, float[] samples)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _outputOscilloscope?.UpdateSamples(samples);
        });
    }

    private void OnEncoderMessage(object? sender, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[{Name}] FFmpeg: {message}");
    }

    private void OnProcessorStopped(object? sender, EventArgs e)
    {
        if (_processor != null)
        {
            _processor.InputSamplesAvailable -= OnInputSamplesAvailable;
            _processor.OutputSamplesAvailable -= OnOutputSamplesAvailable;
            _processor.EncoderMessage -= OnEncoderMessage;
            _processor.Stopped -= OnProcessorStopped;
            _processor = null;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            StatusMessage = "Stopped";
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
