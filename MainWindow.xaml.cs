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

        // Find the oscilloscopes in the visual tree
        var inputScope = FindOscilloscopeByTag(element, "InputScope");
        var outputScope = FindOscilloscopeByTag(element, "OutputScope");

        System.Diagnostics.Debug.WriteLine($"[{streamVm.Name}] Attaching oscilloscopes: Input={inputScope != null}, Output={outputScope != null}");

        if (inputScope != null && outputScope != null)
        {
            streamVm.AttachOscilloscopes(inputScope, outputScope);
        }
    }

    private static OscilloscopeControl? FindOscilloscopeByTag(DependencyObject parent, string tag)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is OscilloscopeControl oscilloscope && oscilloscope.Tag?.ToString() == tag)
            {
                return oscilloscope;
            }

            var result = FindOscilloscopeByTag(child, tag);
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
