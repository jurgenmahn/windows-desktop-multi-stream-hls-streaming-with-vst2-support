using System.IO;
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
