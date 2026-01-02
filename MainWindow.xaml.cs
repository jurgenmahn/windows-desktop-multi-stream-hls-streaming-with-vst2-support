using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioProcessorAndStreamer.Controls;
using AudioProcessorAndStreamer.ViewModels;

namespace AudioProcessorAndStreamer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Load logo from Assets folder
        var logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.png");
        if (File.Exists(logoPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            AppLogo.Source = bitmap;
        }

        // Set window icon from logo.png (WPF can use PNG as icon)
        if (File.Exists(logoPath))
        {
            try
            {
                var iconBitmap = new BitmapImage();
                iconBitmap.BeginInit();
                iconBitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
                iconBitmap.CacheOption = BitmapCacheOption.OnLoad;
                iconBitmap.EndInit();
                Icon = iconBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load window icon: {ex.Message}");
            }
        }

        // Auto-start web server and all streams after UI is fully rendered
        // Small delay ensures all StreamItem_Loaded events have completed
        await Task.Delay(500);

        System.Diagnostics.Debug.WriteLine("[MainWindow] Auto-starting server and streams...");

        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            await vm.StartServerCommand.ExecuteAsync(null);
            vm.StartAllStreamsCommand.Execute(null);
        }
    }

    private void StreamItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not StreamViewModel streamVm) return;

        // Find both spectrum analyzers in the visual tree
        var inputAnalyzer = FindSpectrumAnalyzerByTag(element, "InputSpectrumAnalyzer");
        var outputAnalyzer = FindSpectrumAnalyzerByTag(element, "OutputSpectrumAnalyzer");

        if (inputAnalyzer != null && outputAnalyzer != null)
        {
            streamVm.AttachSpectrumAnalyzers(inputAnalyzer, outputAnalyzer);
        }
    }

    private static SpectrumAnalyzerControl? FindSpectrumAnalyzerByTag(DependencyObject parent, string tag)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is SpectrumAnalyzerControl analyzer && analyzer.Tag?.ToString() == tag)
            {
                return analyzer;
            }

            var result = FindSpectrumAnalyzerByTag(child, tag);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SaveStreamsToConfig();
            vm.Dispose();
        }
    }
}
