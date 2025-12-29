using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioProcessorAndStreamer.Controls;

public partial class OscilloscopeControl : UserControl
{
    private WriteableBitmap? _bitmap;
    private float[] _sampleBuffer = Array.Empty<float>();
    private readonly object _bufferLock = new();
    private int _lastWidth;
    private int _lastHeight;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(OscilloscopeControl),
            new PropertyMetadata(""));

    public static readonly DependencyProperty WaveformColorProperty =
        DependencyProperty.Register(nameof(WaveformColor), typeof(Color), typeof(OscilloscopeControl),
            new PropertyMetadata(Colors.LimeGreen));

    public static readonly DependencyProperty BackgroundColorProperty =
        DependencyProperty.Register(nameof(BackgroundFillColor), typeof(Color), typeof(OscilloscopeControl),
            new PropertyMetadata(Color.FromRgb(0x1A, 0x1A, 0x1A)));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Color WaveformColor
    {
        get => (Color)GetValue(WaveformColorProperty);
        set => SetValue(WaveformColorProperty, value);
    }

    public Color BackgroundFillColor
    {
        get => (Color)GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public OscilloscopeControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateBitmap();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CreateBitmap();
    }

    private void CreateBitmap()
    {
        int width = Math.Max(1, (int)ActualWidth);
        int height = Math.Max(1, (int)ActualHeight);

        if (width == _lastWidth && height == _lastHeight)
            return;

        _lastWidth = width;
        _lastHeight = height;

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        WaveformImage.Source = _bitmap;
    }

    public void UpdateSamples(float[] samples)
    {
        lock (_bufferLock)
        {
            _sampleBuffer = samples;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_bitmap == null || !IsVisible) return;

        float[] samples;
        lock (_bufferLock)
        {
            samples = _sampleBuffer;
        }

        RenderWaveform(samples);
    }

    private void RenderWaveform(float[] samples)
    {
        if (_bitmap == null) return;

        int width = _bitmap.PixelWidth;
        int height = _bitmap.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[height * stride];

        // Fill background
        byte bgR = BackgroundFillColor.R;
        byte bgG = BackgroundFillColor.G;
        byte bgB = BackgroundFillColor.B;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = bgB;
            pixels[i + 1] = bgG;
            pixels[i + 2] = bgR;
            pixels[i + 3] = 255;
        }

        // Draw center line
        int centerY = height / 2;
        for (int x = 0; x < width; x++)
        {
            int offset = (centerY * stride) + (x * 4);
            pixels[offset] = 0x40;
            pixels[offset + 1] = 0x40;
            pixels[offset + 2] = 0x40;
        }

        if (samples.Length > 0)
        {
            byte wfR = WaveformColor.R;
            byte wfG = WaveformColor.G;
            byte wfB = WaveformColor.B;

            // Calculate samples per pixel (decimation)
            float samplesPerPixel = (float)samples.Length / width;

            int? lastY = null;

            for (int x = 0; x < width; x++)
            {
                // Find min/max in this pixel's range
                int startSample = (int)(x * samplesPerPixel);
                int endSample = Math.Min((int)((x + 1) * samplesPerPixel), samples.Length);

                float minVal = 0, maxVal = 0;
                for (int i = startSample; i < endSample; i++)
                {
                    if (samples[i] < minVal) minVal = samples[i];
                    if (samples[i] > maxVal) maxVal = samples[i];
                }

                // Convert to pixel coordinates
                int yMin = (int)(centerY - maxVal * (centerY - 1));
                int yMax = (int)(centerY - minVal * (centerY - 1));

                yMin = Math.Clamp(yMin, 0, height - 1);
                yMax = Math.Clamp(yMax, 0, height - 1);

                // Draw vertical line for this sample range
                for (int y = yMin; y <= yMax; y++)
                {
                    int offset = (y * stride) + (x * 4);
                    pixels[offset] = wfB;
                    pixels[offset + 1] = wfG;
                    pixels[offset + 2] = wfR;
                    pixels[offset + 3] = 255;
                }

                // Connect to previous column if there's a gap
                if (lastY.HasValue)
                {
                    int prevY = lastY.Value;
                    int currY = (yMin + yMax) / 2;
                    int minY = Math.Min(prevY, currY);
                    int maxY = Math.Max(prevY, currY);

                    for (int y = minY; y <= maxY; y++)
                    {
                        if (y >= 0 && y < height)
                        {
                            int offset = (y * stride) + ((x - 1) * 4);
                            if (offset >= 0 && offset + 3 < pixels.Length)
                            {
                                pixels[offset] = wfB;
                                pixels[offset + 1] = wfG;
                                pixels[offset + 2] = wfR;
                                pixels[offset + 3] = 255;
                            }
                        }
                    }
                }

                lastY = (yMin + yMax) / 2;
            }
        }

        try
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        }
        catch
        {
            // Bitmap might be disposed
        }
    }
}
