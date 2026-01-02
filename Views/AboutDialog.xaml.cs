using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AudioProcessorAndStreamer.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Set version and build date
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var buildDate = GetBuildDate(assembly);

        VersionText.Text = $"Version {version?.Major}.{version?.Minor}.{version?.Build} - Built {buildDate:MMMM d, yyyy}";

        // Load 9yards logo from Assets folder
        var logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "9yards-logo.png");
        if (File.Exists(logoPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            NineYardsLogo.Source = bitmap;
        }
    }

    private static DateTime GetBuildDate(Assembly assembly)
    {
        // Try to get build date from assembly file
        var location = assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            return File.GetLastWriteTime(location);
        }

        // For single-file deployments, use the executable
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            return File.GetLastWriteTime(exePath);
        }

        return DateTime.Now;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
