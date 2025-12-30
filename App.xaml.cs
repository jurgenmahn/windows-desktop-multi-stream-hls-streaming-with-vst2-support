using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using AudioProcessorAndStreamer.Models;
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
                    config.HlsOutputDirectory ??= "hls_output";
                    config.BaseDomain ??= "http://localhost:8080";
                    config.Streams ??= new List<StreamConfiguration>();
                });

                // Services
                services.AddSingleton<IVstHostService, VstHostService>();
                services.AddSingleton<IFfmpegService, FfmpegService>();
                services.AddSingleton<IStreamManager, StreamManager>();
                services.AddSingleton<HlsWebServer>();

                // ViewModels
                services.AddTransient<MainWindowViewModel>();

                // Main Window
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // Create and show main window
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            // Cleanup
            var streamManager = _host.Services.GetService<IStreamManager>();
            streamManager?.Dispose();

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
