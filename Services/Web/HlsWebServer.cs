using System.IO;
using System.Text;
using System.Collections.Concurrent;
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
    private readonly AppConfiguration _config;
    private readonly List<StreamConfiguration> _streams;
    private WebApplication? _app;
    private bool _isRunning;

    // Track active listeners per stream - maps streamPath -> (clientIP -> lastSeenTime)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _streamListeners = new();
    private readonly TimeSpan _listenerTimeout = TimeSpan.FromSeconds(30);
    private System.Threading.Timer? _cleanupTimer;

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public string BaseUrl => _config.GetFullBaseUrl();

    public event EventHandler<string>? RequestReceived;
    public event EventHandler<string>? ServerError;
    public event EventHandler? ListenerCountChanged;

    public HlsWebServer(IOptions<AppConfiguration> config)
    {
        _config = config.Value;
        _streams = _config.Streams;
        _port = _config.WebServerPort;
        _hlsDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            _config.HlsOutputDirectory);
    }

    public HlsWebServer(int port, string hlsDirectory)
    {
        _port = port;
        _hlsDirectory = hlsDirectory;
        _config = new AppConfiguration();
        _streams = new List<StreamConfiguration>();
    }

    public void UpdateStreams(List<StreamConfiguration> streams)
    {
        _streams.Clear();
        _streams.AddRange(streams);
    }

    public int GetListenerCount(string streamPath)
    {
        if (_streamListeners.TryGetValue(streamPath, out var listeners))
        {
            return listeners.Count;
        }
        return 0;
    }

    public bool HasListeners(string streamPath)
    {
        return GetListenerCount(streamPath) > 0;
    }

    /// <summary>
    /// Gets all streams with their current listener counts.
    /// </summary>
    public Dictionary<string, int> GetAllListenerCounts()
    {
        var result = new Dictionary<string, int>();
        foreach (var kvp in _streamListeners)
        {
            var count = kvp.Value.Count;
            if (count > 0)
            {
                result[kvp.Key] = count;
            }
        }
        return result;
    }

    private void TrackListener(string streamPath, string clientIp)
    {
        var listeners = _streamListeners.GetOrAdd(streamPath, _ => new ConcurrentDictionary<string, DateTime>());
        var isNew = !listeners.ContainsKey(clientIp);
        listeners[clientIp] = DateTime.UtcNow;

        if (isNew)
        {
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CleanupStaleListeners(object? state)
    {
        var now = DateTime.UtcNow;
        var hasChanges = false;

        foreach (var streamKvp in _streamListeners)
        {
            var listeners = streamKvp.Value;
            var staleClients = listeners
                .Where(kvp => now - kvp.Value > _listenerTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var clientIp in staleClients)
            {
                if (listeners.TryRemove(clientIp, out _))
                {
                    hasChanges = true;
                }
            }
        }

        if (hasChanges)
        {
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);
        }
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

        // Request logging and listener tracking
        _app.Use(async (context, next) =>
        {
            RequestReceived?.Invoke(this, $"{context.Request.Method} {context.Request.Path}");

            // Track listeners for HLS segment/playlist requests
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/hls/") && (path.EndsWith(".ts") || path.EndsWith(".m3u8")))
            {
                // Extract stream path from URL: /hls/{streamPath}/...
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var streamPath = parts[1];
                    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    TrackListener(streamPath, clientIp);
                }
            }

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

        // Root endpoint - list available streams (JSON API)
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

        // HTML Streams listing page
        var streamsPagePath = _config.StreamsPagePath?.TrimEnd('/') ?? "/streams";
        if (!streamsPagePath.StartsWith("/")) streamsPagePath = "/" + streamsPagePath;

        _app.MapGet(streamsPagePath, async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            var html = GenerateStreamsPage();
            await context.Response.WriteAsync(html);
        });

        // Health check endpoint
        _app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        try
        {
            await _app.StartAsync();
            _isRunning = true;

            // Start cleanup timer to remove stale listeners every 5 seconds
            _cleanupTimer = new System.Threading.Timer(CleanupStaleListeners, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
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
            // Stop cleanup timer
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;

            // Clear all listener data
            _streamListeners.Clear();
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);

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

    private string GenerateStreamsPage()
    {
        var baseUrl = _config.GetFullBaseUrl();
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>Available Streams</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f5f5f5; padding: 20px; }");
        sb.AppendLine("    h1 { color: #333; margin-bottom: 20px; }");
        sb.AppendLine("    .streams { display: grid; gap: 16px; max-width: 800px; margin: 0 auto; }");
        sb.AppendLine("    .stream { background: white; border-radius: 8px; padding: 16px; display: flex; align-items: center; gap: 16px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); cursor: pointer; transition: transform 0.2s, box-shadow 0.2s; text-decoration: none; color: inherit; }");
        sb.AppendLine("    .stream:hover { transform: translateY(-2px); box-shadow: 0 4px 12px rgba(0,0,0,0.15); }");
        sb.AppendLine("    .stream-logo { width: 64px; height: 64px; border-radius: 8px; object-fit: cover; background: #e0e0e0; display: flex; align-items: center; justify-content: center; color: #999; font-size: 24px; }");
        sb.AppendLine("    .stream-logo img { width: 100%; height: 100%; object-fit: cover; border-radius: 8px; }");
        sb.AppendLine("    .stream-info { flex: 1; }");
        sb.AppendLine("    .stream-name { font-size: 18px; font-weight: 600; color: #333; margin-bottom: 4px; }");
        sb.AppendLine("    .stream-profiles { font-size: 12px; color: #666; }");
        sb.AppendLine("    .profile-badge { display: inline-block; background: #e3f2fd; color: #1976d2; padding: 2px 8px; border-radius: 4px; margin-right: 4px; margin-top: 4px; }");
        sb.AppendLine("    .play-icon { color: #0078d4; font-size: 24px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Available Streams</h1>");
        sb.AppendLine("  <div class=\"streams\">");

        foreach (var stream in _streams.Where(s => s.IsEnabled))
        {
            var streamUrl = $"{baseUrl}/hls/{stream.StreamPath}/master.m3u8";
            var logoHtml = !string.IsNullOrEmpty(stream.LogoPath) && File.Exists(stream.LogoPath)
                ? $"<img src=\"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(stream.LogoPath))}\" alt=\"{stream.Name}\">"
                : "&#9835;";

            sb.AppendLine($"    <a class=\"stream\" href=\"{streamUrl}\" target=\"_blank\">");
            sb.AppendLine($"      <div class=\"stream-logo\">{logoHtml}</div>");
            sb.AppendLine($"      <div class=\"stream-info\">");
            sb.AppendLine($"        <div class=\"stream-name\">{System.Net.WebUtility.HtmlEncode(stream.Name)}</div>");
            sb.AppendLine($"        <div class=\"stream-profiles\">");

            foreach (var profile in stream.EncodingProfiles)
            {
                var bitrateLabel = profile.Bitrate >= 1000
                    ? $"{profile.Bitrate / 1000}kbps"
                    : $"{profile.Bitrate}bps";
                sb.AppendLine($"          <span class=\"profile-badge\">{bitrateLabel} {profile.Codec}</span>");
            }

            sb.AppendLine($"        </div>");
            sb.AppendLine($"      </div>");
            sb.AppendLine($"      <div class=\"play-icon\">&#9654;</div>");
            sb.AppendLine($"    </a>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
