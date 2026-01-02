using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioProcessorAndStreamer.Controls;
using AudioProcessorAndStreamer.ViewModels;
using Forms = System.Windows.Forms;

namespace AudioProcessorAndStreamer;

public partial class MainWindow : Window
{
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isExiting;
    private bool _minimizeToTrayPreference;
    private bool _closeToTrayPreference;
    private bool _minimizePreferenceSet;
    private bool _closePreferenceSet;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        InitializeNotifyIcon();
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

    private void InitializeNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon();

        // Try to load icon from Assets folder
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                _notifyIcon.Icon = new Icon(iconPath);
            }
            catch
            {
                // Fall back to default icon
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        else
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        _notifyIcon.Text = "Audio Processor And Streamer";
        _notifyIcon.Visible = false;

        // Create context menu
        var contextMenu = new Forms.ContextMenuStrip();

        var showItem = new Forms.ToolStripMenuItem("Show Window");
        showItem.Click += (s, e) => RestoreFromTray();
        contextMenu.Items.Add(showItem);

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            // Ask user what they want to do (unless preference already set)
            if (!_minimizePreferenceSet)
            {
                var result = System.Windows.MessageBox.Show(
                    "Do you want to minimize to the system tray instead of the taskbar?\n\n" +
                    "Click 'Yes' to minimize to tray (app continues running in background).\n" +
                    "Click 'No' to minimize to taskbar normally.\n\n" +
                    "Your choice will be remembered for this session.",
                    "Minimize Behavior",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                _minimizeToTrayPreference = result == MessageBoxResult.Yes;
                _minimizePreferenceSet = true;
            }

            if (_minimizeToTrayPreference)
            {
                MinimizeToTray();
            }
        }
    }

    private void MinimizeToTray()
    {
        Hide();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(2000, "Audio Processor And Streamer",
                "Application minimized to system tray. Double-click to restore.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If we're already exiting (from tray menu), don't show prompt
        if (!_isExiting)
        {
            // Ask user what they want to do (unless preference already set)
            if (!_closePreferenceSet)
            {
                var result = System.Windows.MessageBox.Show(
                    "Do you want to minimize to the system tray instead of closing?\n\n" +
                    "Click 'Yes' to minimize to tray (app continues running).\n" +
                    "Click 'No' to close the application completely.\n" +
                    "Click 'Cancel' to go back.\n\n" +
                    "Your choice will be remembered for this session.",
                    "Close Application",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                _closeToTrayPreference = result == MessageBoxResult.Yes;
                _closePreferenceSet = true;
            }

            if (_closeToTrayPreference)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                MinimizeToTray();
                return;
            }
        }

        // Actually closing - cleanup
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.SaveStreamsToConfig();
            vm.Dispose();
        }
    }
}
