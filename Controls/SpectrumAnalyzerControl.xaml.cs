using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Dsp;
using UserControl = System.Windows.Controls.UserControl;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;

namespace AudioProcessorAndStreamer.Controls;

public partial class SpectrumAnalyzerControl : UserControl
{
    private Complex[] _fftBuffer = new Complex[FftSize];
    private float[] _sampleAccumulator = new float[FftSize];
    private float[] _bandValues = new float[BandCount];
    private float[] _smoothedBandValues = new float[BandCount];
    private float[] _peakValues = new float[BandCount];
    private readonly object _bufferLock = new();
    private int _accumulatorIndex;
    private volatile bool _newDataAvailable;
    private int _sampleRate = 48000;
    private bool _isInitialized;
    private DispatcherTimer? _renderTimer;
    private bool _isDisposed;

    // Pre-calculated bar data to minimize UI thread work
    private int[] _barHeights = new int[BandCount];
    private int[] _barColors = new int[BandCount]; // 0=green, 1=yellow, 2=red
    private int[] _peakPositions = new int[BandCount];
    private int[] _peakColors = new int[BandCount];

    // Bar rectangles for each band
    private Rectangle[] _bars = new Rectangle[BandCount];
    private Rectangle[] _peakBars = new Rectangle[BandCount];
    private Rectangle _backgroundRect = null!;

    private const int FftSize = 4096;
    private const int BandCount = 20;
    private const int BarWidth = 15;
    private const int BarSpacing = 2;
    private const int CanvasWidth = BandCount * (BarWidth + BarSpacing) - BarSpacing;
    private const int CanvasHeight = 45;
    private const float SmoothingFactor = 0.35f;
    private const float PeakDecayRate = 0.015f;

    // Reference level for 0dB - calibrated for FFT magnitude output
    // Adjust this value to calibrate the meter (higher = bars lower, lower = bars higher)
    // FFT of normalized audio (-1 to 1) produces magnitudes typically 0.001 to 0.1
    private const float ReferenceLevel = 0.1f;

    // 20 frequency bands
    private static readonly float[] BandFrequencies =
    {
        25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f, 200f,
        250f, 315f, 400f, 500f, 630f, 800f, 1000f, 2000f, 4000f, 8000f
    };

    private static readonly string[] BandLabels =
    {
        "25", "32", "40", "50", "63", "80", "100", "125", "160", "200",
        "250", "315", "400", "500", "630", "800", "1K", "2K", "4K", "8K"
    };

    // Colors - light blue theme
    private static readonly SolidColorBrush BackgroundBrush = new(Color.FromRgb(0x1A, 0x2A, 0x3A)); // Dark blue-ish
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x32, 0xCD, 0x32));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xFF, 0x45, 0x45));
    private static readonly SolidColorBrush PeakGreenBrush = new(Color.FromRgb(0x90, 0xFF, 0x90));
    private static readonly SolidColorBrush PeakYellowBrush = new(Color.FromRgb(0xFF, 0xEE, 0x80));
    private static readonly SolidColorBrush PeakRedBrush = new(Color.FromRgb(0xFF, 0x90, 0x90));

    public string Label
    {
        get => AnalyzerLabel.Text;
        set => AnalyzerLabel.Text = value;
    }

    public SpectrumAnalyzerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        InitializeVisuals();
        FrequencyLabels.ItemsSource = BandLabels;

        // Use a timer at ~25fps instead of CompositionTarget.Rendering (60fps)
        // This reduces UI thread load significantly with multiple analyzers
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40) // 25fps
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void StopRendering()
    {
        if (_renderTimer != null)
        {
            _renderTimer.Stop();
            _renderTimer.Tick -= OnRenderTick;
            _renderTimer = null;
        }
        _isDisposed = true;
    }

    private void InitializeVisuals()
    {
        // Set canvas size
        SpectrumCanvas.Width = CanvasWidth;
        SpectrumCanvas.Height = CanvasHeight;

        // Create background rectangle
        _backgroundRect = new Rectangle
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Fill = BackgroundBrush
        };
        Canvas.SetLeft(_backgroundRect, 0);
        Canvas.SetTop(_backgroundRect, 0);
        SpectrumCanvas.Children.Add(_backgroundRect);

        // Create bars for each band
        for (int i = 0; i < BandCount; i++)
        {
            int x = i * (BarWidth + BarSpacing);

            // Main bar (starts with 0 height at bottom)
            _bars[i] = new Rectangle
            {
                Width = BarWidth,
                Height = 0,
                Fill = GreenBrush
            };
            Canvas.SetLeft(_bars[i], x);
            Canvas.SetTop(_bars[i], CanvasHeight); // Position at bottom (height=0)
            SpectrumCanvas.Children.Add(_bars[i]);

            // Peak indicator bar
            _peakBars[i] = new Rectangle
            {
                Width = BarWidth,
                Height = 2,
                Fill = PeakGreenBrush
            };
            Canvas.SetLeft(_peakBars[i], x);
            Canvas.SetTop(_peakBars[i], CanvasHeight - 2); // Position at bottom
            SpectrumCanvas.Children.Add(_peakBars[i]);
        }
    }

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// Clears all bars and resets the analyzer to initial state.
    /// </summary>
    public void Clear()
    {
        lock (_bufferLock)
        {
            Array.Clear(_sampleAccumulator);
            Array.Clear(_bandValues);
            Array.Clear(_smoothedBandValues);
            Array.Clear(_peakValues);
            Array.Clear(_barHeights);
            Array.Clear(_barColors);
            Array.Clear(_peakPositions);
            Array.Clear(_peakColors);
            _accumulatorIndex = 0;
            _newDataAvailable = false;
        }

        // Reset bar heights on UI thread
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ResetBars, DispatcherPriority.Background);
        }
        else
        {
            ResetBars();
        }
    }

    private void ResetBars()
    {
        if (!_isInitialized) return;

        for (int i = 0; i < BandCount; i++)
        {
            _bars[i].Height = 0;
            Canvas.SetTop(_bars[i], CanvasHeight);
            _bars[i].Fill = GreenBrush;

            Canvas.SetTop(_peakBars[i], CanvasHeight - 2);
            _peakBars[i].Fill = PeakGreenBrush;
        }
    }

    public void UpdateSamples(float[] samples)
    {
        if (samples.Length == 0) return;

        lock (_bufferLock)
        {
            int channels = 2;
            for (int i = 0; i < samples.Length; i += channels)
            {
                float mono = samples[i];
                if (channels == 2 && i + 1 < samples.Length)
                {
                    mono = (samples[i] + samples[i + 1]) / 2f;
                }

                _sampleAccumulator[_accumulatorIndex] = mono;
                _accumulatorIndex++;

                if (_accumulatorIndex >= FftSize)
                {
                    PerformFFT();
                    _accumulatorIndex = 0;
                    _newDataAvailable = true;
                }
            }
        }
    }

    private void PerformFFT()
    {
        // Apply Hanning window
        for (int i = 0; i < FftSize; i++)
        {
            float window = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            _fftBuffer[i].X = _sampleAccumulator[i] * window;
            _fftBuffer[i].Y = 0;
        }

        int m = (int)Math.Log2(FftSize);
        FastFourierTransform.FFT(true, m, _fftBuffer);

        float binSize = (float)_sampleRate / FftSize;

        for (int band = 0; band < BandCount; band++)
        {
            float centerFreq = BandFrequencies[band];
            float lowFreq = centerFreq / 1.26f;
            float highFreq = centerFreq * 1.26f;

            int lowBin = Math.Max(1, (int)(lowFreq / binSize));
            int highBin = Math.Min(FftSize / 2 - 1, (int)(highFreq / binSize));

            float sum = 0;
            int count = 0;

            for (int bin = lowBin; bin <= highBin; bin++)
            {
                float real = _fftBuffer[bin].X;
                float imag = _fftBuffer[bin].Y;
                float magnitude = (float)Math.Sqrt(real * real + imag * imag);
                sum += magnitude;
                count++;
            }

            if (count > 0)
            {
                _bandValues[band] = sum / count;
            }
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isInitialized || !IsVisible) return;

        // Skip if no new data - don't waste CPU
        if (!_newDataAvailable) return;

        // Do smoothing and pre-calculate bar data under lock
        lock (_bufferLock)
        {
            if (!_newDataAvailable) return;

            for (int i = 0; i < BandCount; i++)
            {
                // Smoothing
                _smoothedBandValues[i] = _smoothedBandValues[i] * (1 - SmoothingFactor) + _bandValues[i] * SmoothingFactor;

                // Peak tracking
                if (_smoothedBandValues[i] > _peakValues[i])
                {
                    _peakValues[i] = _smoothedBandValues[i];
                }
                else
                {
                    _peakValues[i] = Math.Max(0, _peakValues[i] - PeakDecayRate);
                }

                // Pre-calculate bar heights and colors (minimize UI thread work)
                float value = _smoothedBandValues[i];
                float dB = value > 0.0001f ? 20f * MathF.Log10(value / ReferenceLevel) : -60f;

                float normalizedHeight = (dB + 48f) / 54f;
                normalizedHeight = Math.Clamp(normalizedHeight, 0, 1);

                _barHeights[i] = (int)(normalizedHeight * CanvasHeight);
                _barColors[i] = dB > 0 ? 2 : (dB > -3 ? 1 : 0);

                // Peak
                float peakDb = _peakValues[i] > 0.0001f ? 20f * MathF.Log10(_peakValues[i] / ReferenceLevel) : -60f;
                float peakNormalized = (peakDb + 48f) / 54f;
                peakNormalized = Math.Clamp(peakNormalized, 0, 1);

                _peakPositions[i] = (int)(peakNormalized * CanvasHeight);
                _peakColors[i] = peakDb > 0 ? 2 : (peakDb > -3 ? 1 : 0);
            }

            _newDataAvailable = false;
        }

        // Now update UI elements - this is the only part that must be on UI thread
        UpdateBarsFromPreCalculated();
    }

    private static readonly SolidColorBrush[] BarBrushes = { GreenBrush, YellowBrush, RedBrush };
    private static readonly SolidColorBrush[] PeakBrushes = { PeakGreenBrush, PeakYellowBrush, PeakRedBrush };

    private void UpdateBarsFromPreCalculated()
    {
        for (int band = 0; band < BandCount; band++)
        {
            int barHeight = _barHeights[band];
            _bars[band].Height = barHeight;
            Canvas.SetTop(_bars[band], CanvasHeight - barHeight);
            _bars[band].Fill = BarBrushes[_barColors[band]];

            Canvas.SetTop(_peakBars[band], CanvasHeight - _peakPositions[band] - 2);
            _peakBars[band].Fill = PeakBrushes[_peakColors[band]];
        }
    }
}
