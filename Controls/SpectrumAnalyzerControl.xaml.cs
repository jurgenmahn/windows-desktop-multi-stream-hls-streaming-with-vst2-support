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
    private int[] _peakPositions = new int[BandCount];

    // Segmented bar rectangles [band][segment]
    private Rectangle[,] _segments = null!;
    private Rectangle[] _peakBars = new Rectangle[BandCount];
    private Rectangle _backgroundRect = null!;

    private const int FftSize = 4096;
    private const int BandCount = 20;
    private const int BarWidth = 15;
    private const int BarSpacing = 2;
    private const int CanvasWidth = BandCount * (BarWidth + BarSpacing) - BarSpacing;
    private const int CanvasHeight = 45;
    private const float SmoothingFactor = 0.35f;
    private const float PeakDecayRate = 0.012f;

    // Segment configuration
    private const int SegmentHeight = 3;
    private const int SegmentGap = 1;
    private const int SegmentStep = SegmentHeight + SegmentGap; // 4px per segment
    private const int SegmentsPerBand = CanvasHeight / SegmentStep; // 11 segments

    // Color zone thresholds (as percentage of total segments)
    private const float GreenZone = 0.60f;  // Bottom 60% is green
    private const float YellowZone = 0.85f; // 60-85% is yellow, 85-100% is red

    // Reference level for 0dB - calibrated for FFT magnitude output
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

    // Colors - background
    private static readonly SolidColorBrush BackgroundBrush = new(Color.FromRgb(0x1A, 0x1A, 0x1A)); // Dark

    // Green theme (default for input)
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x32, 0xCD, 0x32));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xFF, 0x45, 0x45));

    // Orange theme (for output)
    private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(0xFF, 0x8C, 0x00));
    private static readonly SolidColorBrush OrangeYellowBrush = new(Color.FromRgb(0xFF, 0xB3, 0x00));
    private static readonly SolidColorBrush OrangeRedBrush = new(Color.FromRgb(0xFF, 0x45, 0x45));

    // Dependency property for orange theme
    public static readonly DependencyProperty UseOrangeThemeProperty =
        DependencyProperty.Register(nameof(UseOrangeTheme), typeof(bool), typeof(SpectrumAnalyzerControl),
            new PropertyMetadata(false));

    public bool UseOrangeTheme
    {
        get => (bool)GetValue(UseOrangeThemeProperty);
        set => SetValue(UseOrangeThemeProperty, value);
    }

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

        // Initialize segment array
        _segments = new Rectangle[BandCount, SegmentsPerBand];

        // Create segments for each band
        for (int band = 0; band < BandCount; band++)
        {
            int x = band * (BarWidth + BarSpacing);

            // Create segments from bottom to top
            for (int seg = 0; seg < SegmentsPerBand; seg++)
            {
                // Calculate Y position (bottom segment is seg=0)
                int y = CanvasHeight - (seg + 1) * SegmentStep + SegmentGap;

                // Determine color based on segment position
                float segmentPercent = (float)(seg + 1) / SegmentsPerBand;
                SolidColorBrush brush = GetSegmentBrush(segmentPercent);

                var segment = new Rectangle
                {
                    Width = BarWidth,
                    Height = SegmentHeight,
                    Fill = brush,
                    Visibility = Visibility.Hidden // Start hidden
                };
                Canvas.SetLeft(segment, x);
                Canvas.SetTop(segment, y);
                SpectrumCanvas.Children.Add(segment);
                _segments[band, seg] = segment;
            }

            // Peak indicator bar (on top of segments)
            _peakBars[band] = new Rectangle
            {
                Width = BarWidth,
                Height = SegmentHeight,
                Fill = GetSegmentBrush(0), // Will be updated based on position
                Visibility = Visibility.Hidden
            };
            Canvas.SetLeft(_peakBars[band], x);
            Canvas.SetTop(_peakBars[band], CanvasHeight - SegmentHeight);
            SpectrumCanvas.Children.Add(_peakBars[band]);
        }
    }

    private SolidColorBrush GetSegmentBrush(float percentHeight)
    {
        if (UseOrangeTheme)
        {
            if (percentHeight <= GreenZone) return OrangeBrush;
            if (percentHeight <= YellowZone) return OrangeYellowBrush;
            return OrangeRedBrush;
        }
        else
        {
            if (percentHeight <= GreenZone) return GreenBrush;
            if (percentHeight <= YellowZone) return YellowBrush;
            return RedBrush;
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
            Array.Clear(_peakPositions);
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
        if (!_isInitialized || _segments == null) return;

        for (int band = 0; band < BandCount; band++)
        {
            // Hide all segments
            for (int seg = 0; seg < SegmentsPerBand; seg++)
            {
                _segments[band, seg].Visibility = Visibility.Hidden;
            }

            // Hide peak
            _peakBars[band].Visibility = Visibility.Hidden;
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

                // Pre-calculate bar heights (in segments)
                float value = _smoothedBandValues[i];
                float dB = value > 0.0001f ? 20f * MathF.Log10(value / ReferenceLevel) : -60f;

                float normalizedHeight = (dB + 48f) / 54f;
                normalizedHeight = Math.Clamp(normalizedHeight, 0, 1);

                _barHeights[i] = (int)(normalizedHeight * SegmentsPerBand);

                // Peak position (in segments)
                float peakDb = _peakValues[i] > 0.0001f ? 20f * MathF.Log10(_peakValues[i] / ReferenceLevel) : -60f;
                float peakNormalized = (peakDb + 48f) / 54f;
                peakNormalized = Math.Clamp(peakNormalized, 0, 1);

                _peakPositions[i] = (int)(peakNormalized * SegmentsPerBand);
            }

            _newDataAvailable = false;
        }

        // Now update UI elements
        UpdateBarsFromPreCalculated();
    }

    private void UpdateBarsFromPreCalculated()
    {
        if (_segments == null) return;

        for (int band = 0; band < BandCount; band++)
        {
            int activeSegments = _barHeights[band];
            int peakSegment = _peakPositions[band];

            // Update segment visibility
            for (int seg = 0; seg < SegmentsPerBand; seg++)
            {
                bool shouldBeVisible = seg < activeSegments;
                var segment = _segments[band, seg];

                if (shouldBeVisible)
                {
                    if (segment.Visibility != Visibility.Visible)
                    {
                        segment.Visibility = Visibility.Visible;
                        // Update color based on theme (in case theme changed)
                        float segmentPercent = (float)(seg + 1) / SegmentsPerBand;
                        segment.Fill = GetSegmentBrush(segmentPercent);
                    }
                }
                else
                {
                    if (segment.Visibility != Visibility.Hidden)
                    {
                        segment.Visibility = Visibility.Hidden;
                    }
                }
            }

            // Update peak indicator
            if (peakSegment > 0 && peakSegment > activeSegments)
            {
                int peakY = CanvasHeight - peakSegment * SegmentStep + SegmentGap;
                Canvas.SetTop(_peakBars[band], peakY);
                float peakPercent = (float)peakSegment / SegmentsPerBand;
                _peakBars[band].Fill = GetSegmentBrush(peakPercent);
                _peakBars[band].Visibility = Visibility.Visible;
            }
            else
            {
                _peakBars[band].Visibility = Visibility.Hidden;
            }
        }
    }
}
