using System.Windows;
using Application = System.Windows.Application;
using AudioProcessorAndStreamer.Controls;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Audio;
using AudioProcessorAndStreamer.Services.Streaming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioProcessorAndStreamer.ViewModels;

public partial class StreamViewModel : ObservableObject, IDisposable
{
    private readonly StreamConfiguration _config;
    private readonly IStreamManager _streamManager;
    private AudioStreamProcessor? _processor;
    private AudioInputMonitor? _inputMonitor;
    private OscilloscopeControl? _inputOscilloscope;
    private OscilloscopeControl? _outputOscilloscope;
    private bool _disposed;
    private bool _oscilloscopesAttached;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Monitoring";

    [ObservableProperty]
    private bool _vstBypassed;

    public string Id => _config.Id;
    public StreamConfiguration Configuration => _config;

    partial void OnVstBypassedChanged(bool value)
    {
        _processor?.SetVstBypassed(value);
    }

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
        _oscilloscopesAttached = true;

        System.Diagnostics.Debug.WriteLine($"[{Name}] Oscilloscopes attached, IsRunning={IsRunning}");

        // Start input monitoring when oscilloscopes are attached (if not already streaming)
        if (!IsRunning)
        {
            StartInputMonitoring();
        }
    }

    private void StartInputMonitoring()
    {
        if (_inputMonitor != null) return;

        try
        {
            _inputMonitor = new AudioInputMonitor(_config.AudioInput);
            _inputMonitor.SamplesAvailable += OnMonitorSamplesAvailable;
            _inputMonitor.StartMonitoring();
            StatusMessage = "Monitoring";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start input monitor: {ex.Message}");
            StatusMessage = "Monitor failed";
        }
    }

    private void StopInputMonitoring()
    {
        if (_inputMonitor == null) return;

        _inputMonitor.SamplesAvailable -= OnMonitorSamplesAvailable;
        _inputMonitor.Dispose();
        _inputMonitor = null;
    }

    private void OnMonitorSamplesAvailable(object? sender, float[] samples)
    {
        // UpdateSamples is thread-safe, no need to dispatch
        _inputOscilloscope?.UpdateSamples(samples);
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning) return;

        // Stop input monitoring - stream processor will take over
        StopInputMonitoring();

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
            // Restart monitoring since stream failed
            if (_oscilloscopesAttached)
            {
                StartInputMonitoring();
            }
        }
    }

    [RelayCommand]
    public void Stop()
    {
        if (!IsRunning) return;

        _streamManager.StopStream(_config.Id);
    }

    private int _inputSampleCount = 0;
    private void OnInputSamplesAvailable(object? sender, float[] samples)
    {
        // Debug: track input sample delivery (every 100 callbacks)
        if (++_inputSampleCount % 100 == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[{Name}] Input samples received #{_inputSampleCount}: {samples.Length} samples");
        }

        // UpdateSamples is thread-safe, no need to dispatch
        _inputOscilloscope?.UpdateSamples(samples);
    }

    private int _outputSampleCount = 0;
    private void OnOutputSamplesAvailable(object? sender, float[] samples)
    {
        // Debug: track output sample delivery (every 100 callbacks)
        if (++_outputSampleCount % 100 == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[{Name}] Output samples received #{_outputSampleCount}: {samples.Length} samples, oscilloscope={_outputOscilloscope != null}");
        }

        // UpdateSamples is thread-safe, no need to dispatch
        _outputOscilloscope?.UpdateSamples(samples);
    }

    private void OnEncoderMessage(object? sender, string message)
    {
        // Filter out common harmless HLS segment cleanup messages
        if (message.Contains("failed to delete old segment"))
            return;

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

            // Restart input monitoring after stream stops
            if (_oscilloscopesAttached)
            {
                StartInputMonitoring();
            }
            else
            {
                StatusMessage = "Stopped";
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopInputMonitoring();
        Stop();
    }
}
