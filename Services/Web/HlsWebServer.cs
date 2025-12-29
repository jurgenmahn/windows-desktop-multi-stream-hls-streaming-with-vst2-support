using System.IO;
using AudioProcessorAndStreamer.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace AudioProcessorAndStreamer.Services.Web;

public class HlsWebServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _hlsDirectory;
    private WebApplication? _app;
    private bool _isRunning;

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public string BaseUrl => $"http://localhost:{_port}";

    public event EventHandler<string>? RequestReceived;
    public event EventHandler<string>? ServerError;

    public HlsWebServer(IOptions<AppConfiguration> config)
    {
        _port = config.Value.WebServerPort;
        _hlsDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            config.Value.HlsOutputDirectory);
    }

    public HlsWebServer(int port, string hlsDirectory)
    {
        _port = port;
        _hlsDirectory = hlsDirectory;
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        // Ensure HLS directory exists
        Directory.CreateDirectory(_hlsDirectory);

        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
        });

        _app = builder.Build();

        // Enable CORS for HLS clients (browsers, players)
        _app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Range");
            context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Length, Content-Range");

            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            await next();
        });

        // Request logging
        _app.Use(async (context, next) =>
        {
            RequestReceived?.Invoke(this, $"{context.Request.Method} {context.Request.Path}");
            await next();
        });

        // Serve HLS files with custom content types
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_hlsDirectory),
            RequestPath = "/hls",
            ContentTypeProvider = new HlsContentTypeProvider(),
            ServeUnknownFileTypes = false,
            OnPrepareResponse = ctx =>
            {
                // Disable caching for playlist files
                if (ctx.File.Name.EndsWith(".m3u8"))
                {
                    ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                    ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                    ctx.Context.Response.Headers.Append("Expires", "0");
                }
                else
                {
                    // Allow caching for segments
                    ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=3600");
                }
            }
        });

        // Root endpoint - list available streams
        _app.MapGet("/", async context =>
        {
            var streams = new List<object>();

            if (Directory.Exists(_hlsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(_hlsDirectory))
                {
                    var streamName = Path.GetFileName(dir);
                    var playlists = Directory.GetFiles(dir, "*.m3u8")
                        .Select(f => $"/hls/{streamName}/{Path.GetFileName(f)}")
                        .ToList();

                    if (playlists.Count > 0)
                    {
                        streams.Add(new
                        {
                            name = streamName,
                            playlists
                        });
                    }
                }
            }

            await context.Response.WriteAsJsonAsync(new { streams });
        });

        // Health check endpoint
        _app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        try
        {
            await _app.StartAsync();
            _isRunning = true;
        }
        catch (Exception ex)
        {
            ServerError?.Invoke(this, $"Failed to start server: {ex.Message}");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning || _app == null) return;

        try
        {
            await _app.StopAsync();
        }
        finally
        {
            _isRunning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_app != null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
