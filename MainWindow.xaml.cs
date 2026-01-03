using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioProcessorAndStreamer.Controls;
using AudioProcessorAndStreamer.Infrastructure;
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
    private bool _minimizingFromClose; // Flag to prevent double prompt
    private bool _forceClose; // Flag to bypass minimize-to-tray for auto-update

    public MainWindow()
    {
        DebugLogger.Log("MainWindow", "Constructor started");
        InitializeComponent();
        DebugLogger.Log("MainWindow", "InitializeComponent() completed");

        Closing += OnClosing;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        DebugLogger.Log("MainWindow", "Event handlers subscribed");

        InitializeNotifyIcon();
        DebugLogger.Log("MainWindow", "InitializeNotifyIcon() completed");
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

        DebugLogger.Log("MainWindow", "Auto-starting server and streams...");

        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            await vm.StartServerCommand.ExecuteAsync(null);
            vm.StartAllStreamsCommand.Execute(null);

            // Check for updates silently in the background (after startup completes)
            _ = Task.Run(async () =>
            {
                // Wait a bit more before checking for updates to not interfere with startup
                await Task.Delay(2000);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await vm.CheckForUpdatesSilentAsync();
                    // Start periodic update checking (every hour)
                    vm.StartPeriodicUpdateCheck();
                });
            });
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
        DebugLogger.Log("MainWindow", $"OnStateChanged - WindowState: {WindowState}");

        if (WindowState == WindowState.Minimized)
        {
            DebugLogger.Log("MainWindow", $"Window minimized - _minimizePreferenceSet: {_minimizePreferenceSet}, _minimizeToTrayPreference: {_minimizeToTrayPreference}, _minimizingFromClose: {_minimizingFromClose}");

            // If this minimize was triggered from OnClosing, skip the prompt and just minimize to tray
            if (_minimizingFromClose)
            {
                _minimizingFromClose = false;
                MinimizeToTray();
                return;
            }

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
        DebugLogger.Log("MainWindow", "MinimizeToTray() called");
        Hide();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(2000, "Audio Processor And Streamer",
                "Application minimized to system tray. Double-click to restore.",
                Forms.ToolTipIcon.Info);
            DebugLogger.Log("MainWindow", "Window hidden, notify icon visible");
        }
        else
        {
            DebugLogger.Log("MainWindow", "WARNING: _notifyIcon is null!");
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

    /// <summary>
    /// Forces the application to close without showing minimize-to-tray prompts.
    /// Used by auto-updater to close the app before launching the installer.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        _isExiting = true;
        Close();
        // Explicitly shutdown the application to stop all background services (IHost, web server, etc.)
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        DebugLogger.Log("MainWindow", $"OnClosing - _isExiting: {_isExiting}, _forceClose: {_forceClose}, _closePreferenceSet: {_closePreferenceSet}, _closeToTrayPreference: {_closeToTrayPreference}");

        // If force close is set (e.g., from auto-updater), bypass all prompts
        if (_forceClose)
        {
            DebugLogger.Log("MainWindow", "Force close - bypassing minimize-to-tray prompt");
            // Fall through to cleanup and close
        }
        // If we're already exiting (from tray menu), don't show prompt
        else if (!_isExiting)
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
                _minimizingFromClose = true; // Prevent OnStateChanged from showing another prompt
                WindowState = WindowState.Minimized;
                // MinimizeToTray will be called by OnStateChanged
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

        // Explicitly shutdown the application to stop all background services (IHost, web server, etc.)
        System.Windows.Application.Current.Shutdown();
    }
}
