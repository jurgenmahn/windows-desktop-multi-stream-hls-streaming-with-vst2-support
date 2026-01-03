using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using AudioProcessorAndStreamer.Infrastructure;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Audio;
using AudioProcessorAndStreamer.Services.Encoding;
using AudioProcessorAndStreamer.Services.Streaming;
using AudioProcessorAndStreamer.Services.Vst;
using AudioProcessorAndStreamer.Services.Web;
using AudioProcessorAndStreamer.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DebugLogger.Log("FATAL", $"Unhandled exception: {ex?.Message}\n{ex?.StackTrace}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            DebugLogger.Log("FATAL", $"Dispatcher exception: {args.Exception.Message}\n{args.Exception.StackTrace}");
            args.Handled = true; // Prevent crash for debugging
        };

        // Initialize debug logger first
        DebugLogger.Initialize();
        DebugLogger.Log("App", "=== Application Starting ===");
        DebugLogger.Log("App", $"Base directory: {AppDomain.CurrentDomain.BaseDirectory}");
        DebugLogger.Log("App", $"Log file location: {DebugLogger.LogPath}");

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                services.Configure<AppConfiguration>(
                    context.Configuration.GetSection("App"));

                // Ensure default configuration if section is missing
                services.PostConfigure<AppConfiguration>(config =>
                {
                    config.FfmpegPath ??= "FFmpeg/bin/ffmpeg.exe";
                    config.HlsOutputDirectory ??= "stream_output";
                    config.BaseDomain ??= "http://localhost:8080";
                    config.Streams ??= new List<StreamConfiguration>();
                });

                // Services
                services.AddSingleton<IVstHostService, VstHostService>();
                services.AddSingleton<IFfmpegService, FfmpegService>();
                services.AddSingleton<IStreamManager, StreamManager>();
                services.AddSingleton<IMonitorOutputService, MonitorOutputService>();
                services.AddSingleton<HlsWebServer>();

                // ViewModels
                services.AddTransient<MainWindowViewModel>();

                // Main Window
                services.AddSingleton<MainWindow>();
            })
            .Build();

        DebugLogger.Log("App", "Host built, starting...");
        await _host.StartAsync();
        DebugLogger.Log("App", "Host started");

        // Create and show main window
        DebugLogger.Log("App", "Creating MainWindow...");
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        DebugLogger.Log("App", "MainWindow created, setting DataContext...");
        mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
        DebugLogger.Log("App", "DataContext set, showing window...");
        mainWindow.Show();
        DebugLogger.Log("App", "MainWindow shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Start a failsafe background thread that will force termination after 10 seconds
        // This ensures the app exits even if something hangs during cleanup
        var exitThread = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(10000);
            DebugLogger.Log("App", "Failsafe timeout - forcing process termination");
            Environment.Exit(1);
        })
        {
            IsBackground = false, // Must be foreground to survive while cleanup runs
            Name = "Failsafe Exit Thread"
        };
        exitThread.Start();

        try
        {
            DebugLogger.Log("App", "=== Application Exiting ===");

            if (_host != null)
            {
                try
                {
                    // Cleanup services - must be synchronous to ensure completion before exit
                    DebugLogger.Log("App", "Stopping stream manager...");
                    var streamManager = _host.Services.GetService<IStreamManager>();
                    streamManager?.Dispose();
                }
                catch (Exception ex)
                {
                    DebugLogger.Log("App", $"Error stopping stream manager: {ex.Message}");
                }

                try
                {
                    DebugLogger.Log("App", "Stopping monitor service...");
                    var monitorService = _host.Services.GetService<IMonitorOutputService>();
                    monitorService?.Dispose();
                }
                catch (Exception ex)
                {
                    DebugLogger.Log("App", $"Error stopping monitor service: {ex.Message}");
                }

                try
                {
                    DebugLogger.Log("App", "Stopping web server...");
                    var webServer = _host.Services.GetService<HlsWebServer>();
                    if (webServer != null)
                    {
                        webServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log("App", $"Error stopping web server: {ex.Message}");
                }

                try
                {
                    DebugLogger.Log("App", "Stopping host...");
                    _host.StopAsync().Wait(TimeSpan.FromSeconds(5));
                    _host.Dispose();
                }
                catch (Exception ex)
                {
                    DebugLogger.Log("App", $"Error stopping host: {ex.Message}");
                }

                DebugLogger.Log("App", "Cleanup completed");
            }

            base.OnExit(e);
        }
        finally
        {
            // Force process termination - always execute even if exceptions occur
            DebugLogger.Log("App", "Forcing process exit");
            try
            {
                Environment.Exit(0);
            }
            catch
            {
                // If Environment.Exit fails, use Process.Kill as last resort
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }
    }
}
