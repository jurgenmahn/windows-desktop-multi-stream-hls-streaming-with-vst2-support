using System.IO;
using System.Text;
using System.Collections.Concurrent;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.Services.Streaming;
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
    private readonly IStreamManager? _streamManager;
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

    public HlsWebServer(IOptions<AppConfiguration> config, IStreamManager? streamManager = null)
    {
        _config = config.Value;
        _streams = _config.Streams;
        _port = _config.WebServerPort;
        _streamManager = streamManager;
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
        var wasEmpty = listeners.IsEmpty;
        var isNew = !listeners.ContainsKey(clientIp);
        listeners[clientIp] = DateTime.UtcNow;

        if (isNew)
        {
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);

            // Notify StreamManager when first listener connects (for lazy processing)
            if (wasEmpty)
            {
                _streamManager?.OnListenerConnected(streamPath);
            }
        }
    }

    private void CleanupStaleListeners(object? state)
    {
        var now = DateTime.UtcNow;
        var hasChanges = false;
        var streamsWithNoListeners = new List<string>();

        foreach (var streamKvp in _streamListeners)
        {
            var streamPath = streamKvp.Key;
            var listeners = streamKvp.Value;
            var hadListeners = !listeners.IsEmpty;

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

            // Track streams that went from having listeners to none
            if (hadListeners && listeners.IsEmpty)
            {
                streamsWithNoListeners.Add(streamPath);
            }
        }

        if (hasChanges)
        {
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);
        }

        // Notify StreamManager about streams with no listeners (for lazy processing)
        foreach (var streamPath in streamsWithNoListeners)
        {
            _streamManager?.OnNoListeners(streamPath);
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

            // Track listeners for HLS/DASH segment/playlist requests
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/hls/") && (path.EndsWith(".ts") || path.EndsWith(".m3u8") || path.EndsWith(".m4s") || path.EndsWith(".mpd") || path.EndsWith(".mp4")))
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

        // Middleware to wait for segment files when lazy loading (files may not exist immediately)
        _app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            // Only handle HLS/DASH file requests
            if (path.StartsWith("/hls/") && (
                path.EndsWith(".ts") || path.EndsWith(".m4s") || path.EndsWith(".mp4") ||
                path.EndsWith(".m3u8") || path.EndsWith(".mpd")))
            {
                // Convert URL path to file system path
                var relativePath = path.Substring("/hls/".Length);
                var filePath = Path.Combine(_hlsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

                // If file doesn't exist, wait for it (lazy loading support)
                if (!File.Exists(filePath))
                {
                    const int maxWaitMs = 15000;  // Max 15 seconds wait
                    const int pollIntervalMs = 100;  // Check every 100ms
                    var elapsed = 0;

                    while (!File.Exists(filePath) && elapsed < maxWaitMs)
                    {
                        await Task.Delay(pollIntervalMs);
                        elapsed += pollIntervalMs;
                    }

                    // If still doesn't exist after waiting, return 503 Service Unavailable
                    if (!File.Exists(filePath))
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Headers.Append("Retry-After", "2");
                        await context.Response.WriteAsync("Stream is starting, please retry...");
                        return;
                    }
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
                // Disable caching for playlist/manifest files (HLS .m3u8 and DASH .mpd)
                if (ctx.File.Name.EndsWith(".m3u8") || ctx.File.Name.EndsWith(".mpd"))
                {
                    ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                    ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                    ctx.Context.Response.Headers.Append("Expires", "0");
                }
                else
                {
                    // Allow caching for segments (.ts, .m4s, .mp4)
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

        // Serve logo from Assets folder
        _app.MapGet("/assets/logo.png", async context =>
        {
            var logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(logoPath))
            {
                context.Response.ContentType = "image/png";
                context.Response.Headers.Append("Cache-Control", "public, max-age=86400");
                await context.Response.SendFileAsync(logoPath);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        });

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
        sb.AppendLine("  <title>Audio Streams</title>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/hls.js@latest\"></script>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/dashjs@latest/dist/dash.all.min.js\"></script>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: linear-gradient(135deg, #e8f4fc 0%, #d1e8f5 100%); min-height: 100vh; padding: 24px; }");
        sb.AppendLine("    .page-header { text-align: center; margin-bottom: 32px; }");
        sb.AppendLine("    .page-logo { max-height: 80px; margin-bottom: 12px; filter: drop-shadow(0 2px 4px rgba(0,0,0,0.1)); }");
        sb.AppendLine("    .page-title { font-size: 28px; font-weight: 700; color: #1a5276; margin-bottom: 4px; }");
        sb.AppendLine("    .page-subtitle { font-size: 14px; color: #5d6d7e; }");
        sb.AppendLine("    .streams { display: grid; gap: 16px; max-width: 800px; margin: 0 auto; }");
        sb.AppendLine("    .stream { background: white; border-radius: 12px; padding: 20px; box-shadow: 0 4px 12px rgba(0,0,0,0.08); border: 1px solid rgba(255,255,255,0.8); transition: transform 0.2s, box-shadow 0.2s; }");
        sb.AppendLine("    .stream:hover { transform: translateY(-2px); box-shadow: 0 6px 20px rgba(0,0,0,0.12); }");
        sb.AppendLine("    .stream-header { display: flex; align-items: center; gap: 16px; }");
        sb.AppendLine("    .stream-logo { width: 64px; height: 64px; border-radius: 12px; object-fit: cover; background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); display: flex; align-items: center; justify-content: center; color: white; font-size: 24px; flex-shrink: 0; box-shadow: 0 2px 8px rgba(102,126,234,0.3); }");
        sb.AppendLine("    .stream-logo img { width: 100%; height: 100%; object-fit: cover; border-radius: 12px; }");
        sb.AppendLine("    .stream-info { flex: 1; min-width: 0; }");
        sb.AppendLine("    .stream-name { font-size: 18px; font-weight: 600; color: #2c3e50; margin-bottom: 6px; }");
        sb.AppendLine("    .stream-profiles { font-size: 12px; color: #666; }");
        sb.AppendLine("    .profile-badge { display: inline-block; background: linear-gradient(135deg, #e3f2fd 0%, #bbdefb 100%); color: #1565c0; padding: 3px 10px; border-radius: 20px; margin-right: 4px; margin-top: 4px; font-weight: 500; }");
        sb.AppendLine("    .format-badge { display: inline-block; background: linear-gradient(135deg, #fff3e0 0%, #ffe0b2 100%); color: #e65100; padding: 3px 10px; border-radius: 20px; margin-right: 4px; margin-top: 4px; font-weight: 600; }");
        sb.AppendLine("    .container-badge { display: inline-block; background: linear-gradient(135deg, #f3e5f5 0%, #e1bee7 100%); color: #7b1fa2; padding: 3px 10px; border-radius: 20px; margin-right: 4px; margin-top: 4px; font-weight: 500; }");
        sb.AppendLine("    .stream-actions { display: flex; gap: 8px; flex-shrink: 0; }");
        sb.AppendLine("    .btn { padding: 10px 20px; border: none; border-radius: 8px; cursor: pointer; font-size: 14px; font-weight: 600; display: flex; align-items: center; gap: 6px; transition: all 0.2s; }");
        sb.AppendLine("    .btn-play { background: linear-gradient(135deg, #0078d4 0%, #106ebe 100%); color: white; box-shadow: 0 2px 8px rgba(0,120,212,0.3); }");
        sb.AppendLine("    .btn-play:hover { background: linear-gradient(135deg, #106ebe 0%, #005a9e 100%); transform: translateY(-1px); box-shadow: 0 4px 12px rgba(0,120,212,0.4); }");
        sb.AppendLine("    .btn-play.playing { background: linear-gradient(135deg, #d13438 0%, #a4262c 100%); box-shadow: 0 2px 8px rgba(209,52,56,0.3); }");
        sb.AppendLine("    .btn-play.playing:hover { background: linear-gradient(135deg, #e81123 0%, #c42b1c 100%); box-shadow: 0 4px 12px rgba(209,52,56,0.4); }");
        sb.AppendLine("    .btn-copy { background: linear-gradient(135deg, #f0f0f0 0%, #e0e0e0 100%); color: #333; }");
        sb.AppendLine("    .btn-copy:hover { background: linear-gradient(135deg, #e0e0e0 0%, #d0d0d0 100%); }");
        sb.AppendLine("    .btn-copy.copied { background: linear-gradient(135deg, #4caf50 0%, #45a049 100%); color: white; }");
        sb.AppendLine("    .player-container { margin-top: 16px; display: none; }");
        sb.AppendLine("    .player-container.active { display: block; }");
        sb.AppendLine("    .player-controls { display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: linear-gradient(135deg, #f8f9fa 0%, #e9ecef 100%); border-radius: 10px; border: 1px solid #dee2e6; }");
        sb.AppendLine("    .quality-select { padding: 6px 10px; border: 1px solid #ced4da; border-radius: 6px; background: white; font-size: 12px; cursor: pointer; }");
        sb.AppendLine("    .audio-player { display: none; }");
        sb.AppendLine("    .live-indicator { display: flex; align-items: center; gap: 6px; padding: 6px 14px; background: linear-gradient(135deg, #d13438 0%, #a4262c 100%); color: white; border-radius: 20px; font-size: 12px; font-weight: 600; box-shadow: 0 2px 6px rgba(209,52,56,0.3); }");
        sb.AppendLine("    .live-dot { width: 8px; height: 8px; background: white; border-radius: 50%; animation: pulse 1.5s infinite; }");
        sb.AppendLine("    @keyframes pulse { 0%, 100% { opacity: 1; transform: scale(1); } 50% { opacity: 0.6; transform: scale(0.9); } }");
        sb.AppendLine("    .play-time { font-size: 14px; color: #495057; font-weight: 600; font-variant-numeric: tabular-nums; min-width: 60px; }");
        sb.AppendLine("    .spacer { flex: 1; }");
        sb.AppendLine("    .quality-label { font-size: 12px; color: #6c757d; margin-right: 4px; }");
        sb.AppendLine("    .no-streams { text-align: center; padding: 40px; color: #6c757d; }");
        sb.AppendLine("    .no-streams-icon { font-size: 48px; margin-bottom: 12px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"page-header\">");
        sb.AppendLine("    <img src=\"/assets/logo.png\" alt=\"Logo\" class=\"page-logo\" onerror=\"this.style.display='none'\">");
        sb.AppendLine("    <h1 class=\"page-title\">Audio Streams</h1>");
        sb.AppendLine("    <p class=\"page-subtitle\">Select a stream to start listening</p>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <div class=\"streams\">");

        var streamIndex = 0;
        foreach (var stream in _streams.Where(s => s.IsEnabled))
        {
            // Determine URL based on stream format
            var isDash = stream.StreamFormat == StreamFormat.Dash;
            string manifestFile;
            if (isDash)
            {
                // For DASH, use the first profile's MPD (each profile has its own MPD)
                var firstProfile = stream.EncodingProfiles.FirstOrDefault();
                manifestFile = firstProfile != null
                    ? $"{firstProfile.Name.ToLowerInvariant().Replace(" ", "_")}.mpd"
                    : "stream.mpd";
            }
            else
            {
                manifestFile = "master.m3u8";
            }
            var streamUrl = $"{baseUrl}/hls/{stream.StreamPath}/{manifestFile}";
            var formatType = isDash ? "dash" : "hls";
            var logoHtml = !string.IsNullOrEmpty(stream.LogoPath) && File.Exists(stream.LogoPath)
                ? $"<img src=\"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(stream.LogoPath))}\" alt=\"{System.Net.WebUtility.HtmlEncode(stream.Name)}\">"
                : "&#9835;";

            // Format labels for display
            var streamFormatLabel = isDash ? "DASH" : "HLS";
            var containerFormatLabel = stream.ContainerFormat == ContainerFormat.Fmp4 ? "fMP4" : "MPEG-TS";

            sb.AppendLine($"    <div class=\"stream\" data-url=\"{streamUrl}\" data-format=\"{formatType}\" data-index=\"{streamIndex}\">");
            sb.AppendLine($"      <div class=\"stream-header\">");
            sb.AppendLine($"        <div class=\"stream-logo\">{logoHtml}</div>");
            sb.AppendLine($"        <div class=\"stream-info\">");
            sb.AppendLine($"          <div class=\"stream-name\">{System.Net.WebUtility.HtmlEncode(stream.Name)}</div>");
            sb.AppendLine($"          <div class=\"stream-profiles\">");
            sb.AppendLine($"            <span class=\"format-badge\">{streamFormatLabel}</span>");
            sb.AppendLine($"            <span class=\"container-badge\">{containerFormatLabel}</span>");

            foreach (var profile in stream.EncodingProfiles)
            {
                var bitrateLabel = profile.Bitrate >= 1000
                    ? $"{profile.Bitrate / 1000}kbps"
                    : $"{profile.Bitrate}bps";
                sb.AppendLine($"            <span class=\"profile-badge\">{bitrateLabel} {profile.Codec}</span>");
            }

            sb.AppendLine($"          </div>");
            sb.AppendLine($"        </div>");
            sb.AppendLine($"        <div class=\"stream-actions\">");
            sb.AppendLine($"          <button class=\"btn btn-play\" onclick=\"togglePlay({streamIndex})\"><span class=\"play-icon\">&#9654;</span> <span class=\"play-text\">Play</span></button>");
            sb.AppendLine($"          <button class=\"btn btn-copy\" onclick=\"copyUrl({streamIndex}, '{streamUrl}')\">Copy URL</button>");
            sb.AppendLine($"        </div>");
            sb.AppendLine($"      </div>");
            sb.AppendLine($"      <div class=\"player-container\" id=\"player-{streamIndex}\">");
            sb.AppendLine($"        <div class=\"player-controls\">");
            sb.AppendLine($"          <audio class=\"audio-player\" id=\"audio-{streamIndex}\"></audio>");
            sb.AppendLine($"          <div class=\"live-indicator\"><span class=\"live-dot\"></span>LIVE</div>");
            sb.AppendLine($"          <span class=\"play-time\" id=\"time-{streamIndex}\">0:00</span>");
            sb.AppendLine($"          <span class=\"spacer\"></span>");
            sb.AppendLine($"          <span class=\"quality-label\">Quality:</span>");
            sb.AppendLine($"          <select class=\"quality-select\" id=\"quality-{streamIndex}\" onchange=\"changeQuality({streamIndex})\">");
            sb.AppendLine($"            <option value=\"-1\">Auto</option>");
            var profileIndex = 0;
            foreach (var profile in stream.EncodingProfiles)
            {
                var bitrateLabel = profile.Bitrate >= 1000
                    ? $"{profile.Bitrate / 1000}kbps"
                    : $"{profile.Bitrate}bps";
                var codecLabel = profile.Codec.ToString().ToUpper();
                sb.AppendLine($"            <option value=\"{profileIndex}\">{bitrateLabel} {codecLabel}</option>");
                profileIndex++;
            }
            sb.AppendLine($"          </select>");
            sb.AppendLine($"        </div>");
            sb.AppendLine($"      </div>");
            sb.AppendLine($"    </div>");
            streamIndex++;
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine("    const players = {};");
        sb.AppendLine("    const timers = {};");
        sb.AppendLine("    ");
        sb.AppendLine("    function formatTime(seconds) {");
        sb.AppendLine("      const hrs = Math.floor(seconds / 3600);");
        sb.AppendLine("      const mins = Math.floor((seconds % 3600) / 60);");
        sb.AppendLine("      const secs = seconds % 60;");
        sb.AppendLine("      if (hrs > 0) {");
        sb.AppendLine("        return `${hrs}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;");
        sb.AppendLine("      }");
        sb.AppendLine("      return `${mins}:${secs.toString().padStart(2, '0')}`;");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function startTimer(index) {");
        sb.AppendLine("      let seconds = 0;");
        sb.AppendLine("      const timeEl = document.getElementById(`time-${index}`);");
        sb.AppendLine("      timeEl.textContent = formatTime(seconds);");
        sb.AppendLine("      timers[index] = setInterval(() => {");
        sb.AppendLine("        seconds++;");
        sb.AppendLine("        timeEl.textContent = formatTime(seconds);");
        sb.AppendLine("      }, 1000);");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function stopTimer(index) {");
        sb.AppendLine("      if (timers[index]) {");
        sb.AppendLine("        clearInterval(timers[index]);");
        sb.AppendLine("        delete timers[index];");
        sb.AppendLine("      }");
        sb.AppendLine("      const timeEl = document.getElementById(`time-${index}`);");
        sb.AppendLine("      timeEl.textContent = '0:00';");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function togglePlay(index) {");
        sb.AppendLine("      const stream = document.querySelector(`[data-index=\"${index}\"]`);");
        sb.AppendLine("      const playerContainer = document.getElementById(`player-${index}`);");
        sb.AppendLine("      const audio = document.getElementById(`audio-${index}`);");
        sb.AppendLine("      const btn = stream.querySelector('.btn-play');");
        sb.AppendLine("      const btnText = btn.querySelector('.play-text');");
        sb.AppendLine("      const btnIcon = btn.querySelector('.play-icon');");
        sb.AppendLine("      ");
        sb.AppendLine("      if (playerContainer.classList.contains('active')) {");
        sb.AppendLine("        // Stop playing");
        sb.AppendLine("        stopTimer(index);");
        sb.AppendLine("        if (players[index]) {");
        sb.AppendLine("          if (players[index].destroy) players[index].destroy();");
        sb.AppendLine("          else if (players[index].reset) players[index].reset();");
        sb.AppendLine("          delete players[index];");
        sb.AppendLine("        }");
        sb.AppendLine("        audio.pause();");
        sb.AppendLine("        audio.src = '';");
        sb.AppendLine("        playerContainer.classList.remove('active');");
        sb.AppendLine("        btn.classList.remove('playing');");
        sb.AppendLine("        btnText.textContent = 'Play';");
        sb.AppendLine("        btnIcon.innerHTML = '&#9654;';");
        sb.AppendLine("      } else {");
        sb.AppendLine("        // Start playing");
        sb.AppendLine("        const url = stream.dataset.url;");
        sb.AppendLine("        const format = stream.dataset.format;");
        sb.AppendLine("        playerContainer.classList.add('active');");
        sb.AppendLine("        btn.classList.add('playing');");
        sb.AppendLine("        btnText.textContent = 'Stop';");
        sb.AppendLine("        btnIcon.innerHTML = '&#9632;';");
        sb.AppendLine("        initPlayer(index, url, format);");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function initPlayer(index, url, format) {");
        sb.AppendLine("      const audio = document.getElementById(`audio-${index}`);");
        sb.AppendLine("      const qualitySelect = document.getElementById(`quality-${index}`);");
        sb.AppendLine("      ");
        sb.AppendLine("      if (format === 'dash') {");
        sb.AppendLine("        // DASH playback using dash.js");
        sb.AppendLine("        if (typeof dashjs !== 'undefined') {");
        sb.AppendLine("          const player = dashjs.MediaPlayer().create();");
        sb.AppendLine("          player.initialize(audio, url, true);");
        sb.AppendLine("          player.updateSettings({");
        sb.AppendLine("            streaming: {");
        sb.AppendLine("              delay: { liveDelay: 4 },");
        sb.AppendLine("              liveCatchup: { enabled: true }");
        sb.AppendLine("            }");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          player.on(dashjs.MediaPlayer.events.STREAM_INITIALIZED, function() {");
        sb.AppendLine("            startTimer(index);");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          player.on(dashjs.MediaPlayer.events.ERROR, function(e) {");
        sb.AppendLine("            console.error('DASH error:', e);");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          // Quality selection for DASH - disabled for now (auto ABR)");
        sb.AppendLine("          qualitySelect.disabled = true;");
        sb.AppendLine("          players[index] = player;");
        sb.AppendLine("        } else {");
        sb.AppendLine("          alert('DASH playback is not supported in this browser');");
        sb.AppendLine("          togglePlay(index);");
        sb.AppendLine("        }");
        sb.AppendLine("      } else {");
        sb.AppendLine("        // HLS playback using hls.js");
        sb.AppendLine("        if (Hls.isSupported()) {");
        sb.AppendLine("          const hls = new Hls({");
        sb.AppendLine("            enableWorker: true,");
        sb.AppendLine("            lowLatencyMode: true,");
        sb.AppendLine("            liveDurationInfinity: true,");
        sb.AppendLine("            liveBackBufferLength: 30,");
        sb.AppendLine("            backBufferLength: 90");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          hls.loadSource(url);");
        sb.AppendLine("          hls.attachMedia(audio);");
        sb.AppendLine("          ");
        sb.AppendLine("          hls.on(Hls.Events.MANIFEST_PARSED, function(event, data) {");
        sb.AppendLine("            audio.play().then(() => startTimer(index)).catch(e => console.log('Autoplay prevented:', e));");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          hls.on(Hls.Events.LEVEL_SWITCHED, function(event, data) {");
        sb.AppendLine("            // Update quality selector when auto-switching");
        sb.AppendLine("            if (hls.autoLevelEnabled) {");
        sb.AppendLine("              qualitySelect.value = '-1';");
        sb.AppendLine("            }");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          hls.on(Hls.Events.ERROR, function(event, data) {");
        sb.AppendLine("            console.error('HLS error:', data);");
        sb.AppendLine("            if (data.fatal) {");
        sb.AppendLine("              switch(data.type) {");
        sb.AppendLine("                case Hls.ErrorTypes.NETWORK_ERROR:");
        sb.AppendLine("                  hls.startLoad();");
        sb.AppendLine("                  break;");
        sb.AppendLine("                case Hls.ErrorTypes.MEDIA_ERROR:");
        sb.AppendLine("                  hls.recoverMediaError();");
        sb.AppendLine("                  break;");
        sb.AppendLine("                default:");
        sb.AppendLine("                  togglePlay(index);");
        sb.AppendLine("                  break;");
        sb.AppendLine("              }");
        sb.AppendLine("            }");
        sb.AppendLine("          });");
        sb.AppendLine("          ");
        sb.AppendLine("          players[index] = hls;");
        sb.AppendLine("        } else if (audio.canPlayType('application/vnd.apple.mpegurl')) {");
        sb.AppendLine("          // Native HLS support (Safari) - quality selection not available");
        sb.AppendLine("          audio.src = url;");
        sb.AppendLine("          audio.addEventListener('loadedmetadata', function() {");
        sb.AppendLine("            audio.play().then(() => startTimer(index)).catch(e => console.log('Autoplay prevented:', e));");
        sb.AppendLine("          });");
        sb.AppendLine("          qualitySelect.disabled = true;");
        sb.AppendLine("        } else {");
        sb.AppendLine("          alert('HLS is not supported in this browser');");
        sb.AppendLine("          togglePlay(index);");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function changeQuality(index) {");
        sb.AppendLine("      const qualitySelect = document.getElementById(`quality-${index}`);");
        sb.AppendLine("      const level = parseInt(qualitySelect.value);");
        sb.AppendLine("      ");
        sb.AppendLine("      if (players[index]) {");
        sb.AppendLine("        if (level === -1) {");
        sb.AppendLine("          players[index].currentLevel = -1; // Auto");
        sb.AppendLine("        } else {");
        sb.AppendLine("          players[index].currentLevel = level;");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function copyUrl(index, url) {");
        sb.AppendLine("      const btn = document.querySelector(`[data-index=\"${index}\"] .btn-copy`);");
        sb.AppendLine("      ");
        sb.AppendLine("      function showSuccess() {");
        sb.AppendLine("        const originalText = btn.textContent;");
        sb.AppendLine("        btn.textContent = 'Copied!';");
        sb.AppendLine("        btn.classList.add('copied');");
        sb.AppendLine("        setTimeout(() => {");
        sb.AppendLine("          btn.textContent = originalText;");
        sb.AppendLine("          btn.classList.remove('copied');");
        sb.AppendLine("        }, 2000);");
        sb.AppendLine("      }");
        sb.AppendLine("      ");
        sb.AppendLine("      function fallbackCopy() {");
        sb.AppendLine("        const textArea = document.createElement('textarea');");
        sb.AppendLine("        textArea.value = url;");
        sb.AppendLine("        textArea.style.position = 'fixed';");
        sb.AppendLine("        textArea.style.left = '-9999px';");
        sb.AppendLine("        textArea.style.top = '0';");
        sb.AppendLine("        document.body.appendChild(textArea);");
        sb.AppendLine("        textArea.focus();");
        sb.AppendLine("        textArea.select();");
        sb.AppendLine("        try {");
        sb.AppendLine("          document.execCommand('copy');");
        sb.AppendLine("          showSuccess();");
        sb.AppendLine("        } catch (err) {");
        sb.AppendLine("          prompt('Copy this URL:', url);");
        sb.AppendLine("        }");
        sb.AppendLine("        document.body.removeChild(textArea);");
        sb.AppendLine("      }");
        sb.AppendLine("      ");
        sb.AppendLine("      if (navigator.clipboard && navigator.clipboard.writeText) {");
        sb.AppendLine("        navigator.clipboard.writeText(url).then(showSuccess).catch(fallbackCopy);");
        sb.AppendLine("      } else {");
        sb.AppendLine("        fallbackCopy();");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
