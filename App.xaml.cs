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

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            // Cleanup
            var streamManager = _host.Services.GetService<IStreamManager>();
            streamManager?.Dispose();

            var monitorService = _host.Services.GetService<IMonitorOutputService>();
            monitorService?.Dispose();

            var webServer = _host.Services.GetService<HlsWebServer>();
            if (webServer != null)
            {
                await webServer.DisposeAsync();
            }

            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
