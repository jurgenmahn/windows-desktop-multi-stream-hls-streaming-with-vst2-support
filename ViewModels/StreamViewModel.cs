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
    private readonly IMonitorOutputService _monitorService;
    private AudioStreamProcessor? _processor;
    private AudioInputMonitor? _inputMonitor;
    private SpectrumAnalyzerControl? _inputSpectrumAnalyzer;
    private SpectrumAnalyzerControl? _outputSpectrumAnalyzer;
    private bool _disposed;
    private bool _spectrumAnalyzersAttached;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Monitoring";

    [ObservableProperty]
    private bool _isReloading;

    [ObservableProperty]
    private bool _vstEnabled = true;

    [ObservableProperty]
    private bool _monitorEnabled;

    public string Id => _config.Id;
    public StreamConfiguration Configuration => _config;
    public bool HasVstPlugins => _config.VstPlugins.Count > 0;

    partial void OnVstEnabledChanged(bool value)
    {
        // VstEnabled = true means VST is active (not bypassed)
        // VstEnabled = false means VST is bypassed
        _processor?.SetVstBypassed(!value);
    }

    partial void OnMonitorEnabledChanged(bool value)
    {
        if (value)
        {
            // Start monitoring this stream
            var sampleRate = _processor?.ActualSampleRate ?? _config.AudioInput.SampleRate;
            var channels = 2; // Assume stereo
            _monitorService.StartMonitoring(_config.Id, sampleRate, channels);
        }
        else
        {
            // Stop monitoring this stream
            _monitorService.StopMonitoring(_config.Id);
        }
    }

    public StreamViewModel(StreamConfiguration config, IStreamManager streamManager, IMonitorOutputService monitorService)
    {
        _config = config;
        _streamManager = streamManager;
        _monitorService = monitorService;
        _name = config.Name;

        // Subscribe to monitor changes to update our state when another stream takes over
        _monitorService.MonitoredStreamChanged += OnMonitoredStreamChanged;
    }

    private void OnMonitoredStreamChanged(object? sender, string? activeStreamId)
    {
        // Update our MonitorEnabled state based on whether we're the active stream
        // Use dispatcher since this may come from another thread
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Directly set backing field to avoid triggering OnMonitorEnabledChanged again
            if (_monitorEnabled != (activeStreamId == _config.Id))
            {
                _monitorEnabled = activeStreamId == _config.Id;
                OnPropertyChanged(nameof(MonitorEnabled));
            }
        });
    }

    public void AttachSpectrumAnalyzers(SpectrumAnalyzerControl inputAnalyzer, SpectrumAnalyzerControl outputAnalyzer)
    {
        _inputSpectrumAnalyzer = inputAnalyzer;
        _outputSpectrumAnalyzer = outputAnalyzer;
        _spectrumAnalyzersAttached = true;

        // Set sample rate from config
        inputAnalyzer.SetSampleRate(_config.AudioInput.SampleRate);
        outputAnalyzer.SetSampleRate(_config.AudioInput.SampleRate);

        // Start input monitoring when spectrum analyzers are attached (if not already streaming)
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
        // Only update input analyzer when monitoring (not streaming)
        _inputSpectrumAnalyzer?.UpdateSamples(samples);
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

            // Apply current VST enabled state
            _processor.SetVstBypassed(!VstEnabled);

            // Warn user about sample rate mismatch
            if (_processor.HasSampleRateMismatch)
            {
                var message = $"Sample rate mismatch detected!\n\n" +
                              $"Configured: {_processor.ConfiguredSampleRate} Hz\n" +
                              $"Device actual: {_processor.ActualSampleRate} Hz\n\n" +
                              $"Using device rate ({_processor.ActualSampleRate} Hz) for correct playback.\n" +
                              $"Consider updating the stream configuration to match your device.";

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    System.Windows.MessageBox.Show(message, "Sample Rate Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }

            IsRunning = true;
            StatusMessage = "Streaming";
        }
        else
        {
            StatusMessage = "Failed to start";
            // Restart monitoring since stream failed
            if (_spectrumAnalyzersAttached)
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

    private void OnInputSamplesAvailable(object? sender, float[] samples)
    {
        // UpdateSamples is thread-safe, no need to dispatch
        _inputSpectrumAnalyzer?.UpdateSamples(samples);
    }

    private void OnOutputSamplesAvailable(object? sender, float[] samples)
    {
        // UpdateSamples is thread-safe, no need to dispatch
        // Output spectrum analyzer shows post-VST audio
        _outputSpectrumAnalyzer?.UpdateSamples(samples);

        // Send to monitor output if this stream is being monitored
        _monitorService.WriteSamples(_config.Id, samples);
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
            if (_spectrumAnalyzersAttached)
            {
                StartInputMonitoring();
            }
            else
            {
                StatusMessage = "Stopped";
            }
        });
    }

    /// <summary>
    /// Prepares the stream for config reload by stopping it and showing reload status.
    /// </summary>
    public void PrepareForReload()
    {
        IsReloading = true;
        StatusMessage = "Reloading...";

        // Stop input monitoring to prevent spectrum analyzer updates during reload
        StopInputMonitoring();

        // Clear spectrum analyzers
        _inputSpectrumAnalyzer?.Clear();
        _outputSpectrumAnalyzer?.Clear();
    }

    /// <summary>
    /// Sets the stream to waiting state before restart.
    /// </summary>
    public void SetWaitingForRestart()
    {
        StatusMessage = "Waiting...";
    }

    /// <summary>
    /// Completes the reload process and restarts streaming if needed.
    /// </summary>
    public async Task CompleteReloadAsync(bool shouldRestart)
    {
        IsReloading = false;

        if (shouldRestart)
        {
            StatusMessage = "Starting...";
            // Small delay to allow UI to update
            await Task.Delay(50);

            // Start the stream asynchronously
            await StartAsync();
        }
        else
        {
            // Restart input monitoring for spectrum analyzer
            if (_spectrumAnalyzersAttached)
            {
                await StartInputMonitoringAsync();
            }
            else
            {
                StatusMessage = "Stopped";
            }
        }
    }

    /// <summary>
    /// Async version of Start that runs heavy operations on background thread.
    /// </summary>
    private async Task StartAsync()
    {
        if (IsRunning) return;

        // Stop input monitoring first (quick operation)
        StopInputMonitoring();

        // Start the stream on background thread - this is the heavy part
        var processor = await Task.Run(() => _streamManager.StartStream(_config));

        if (processor != null)
        {
            _processor = processor;
            _processor.InputSamplesAvailable += OnInputSamplesAvailable;
            _processor.OutputSamplesAvailable += OnOutputSamplesAvailable;
            _processor.EncoderMessage += OnEncoderMessage;
            _processor.Stopped += OnProcessorStopped;

            // Apply current VST enabled state
            _processor.SetVstBypassed(!VstEnabled);

            // Check for sample rate mismatch
            if (_processor.HasSampleRateMismatch)
            {
                var message = $"Sample rate mismatch detected!\n\n" +
                              $"Configured: {_processor.ConfiguredSampleRate} Hz\n" +
                              $"Device actual: {_processor.ActualSampleRate} Hz\n\n" +
                              $"Using device rate ({_processor.ActualSampleRate} Hz) for correct playback.\n" +
                              $"Consider updating the stream configuration to match your device.";

                System.Windows.MessageBox.Show(message, "Sample Rate Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            IsRunning = true;
            StatusMessage = "Streaming";
        }
        else
        {
            StatusMessage = "Failed to start";
            // Restart monitoring since stream failed
            if (_spectrumAnalyzersAttached)
            {
                await StartInputMonitoringAsync();
            }
        }
    }

    /// <summary>
    /// Async version of StartInputMonitoring.
    /// </summary>
    private async Task StartInputMonitoringAsync()
    {
        if (_inputMonitor != null) return;

        try
        {
            // Create the monitor on background thread
            var monitor = await Task.Run(() => new AudioInputMonitor(_config.AudioInput));

            _inputMonitor = monitor;
            _inputMonitor.SamplesAvailable += OnMonitorSamplesAvailable;

            // Start monitoring on background thread
            await Task.Run(() => _inputMonitor.StartMonitoring());

            StatusMessage = "Monitoring";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start input monitor: {ex.Message}");
            StatusMessage = "Monitor failed";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorService.MonitoredStreamChanged -= OnMonitoredStreamChanged;
        _monitorService.StopMonitoring(_config.Id);

        StopInputMonitoring();
        Stop();
    }
}
