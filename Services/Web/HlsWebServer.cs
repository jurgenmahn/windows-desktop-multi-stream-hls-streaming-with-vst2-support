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
        sb.AppendLine("  <title>Streams</title>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/hls.js@latest\"></script>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f5f5f5; padding: 20px; }");
        sb.AppendLine("    .streams { display: grid; gap: 16px; max-width: 800px; margin: 0 auto; }");
        sb.AppendLine("    .stream { background: white; border-radius: 8px; padding: 16px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
        sb.AppendLine("    .stream-header { display: flex; align-items: center; gap: 16px; }");
        sb.AppendLine("    .stream-logo { width: 64px; height: 64px; border-radius: 8px; object-fit: cover; background: #e0e0e0; display: flex; align-items: center; justify-content: center; color: #999; font-size: 24px; flex-shrink: 0; }");
        sb.AppendLine("    .stream-logo img { width: 100%; height: 100%; object-fit: cover; border-radius: 8px; }");
        sb.AppendLine("    .stream-info { flex: 1; min-width: 0; }");
        sb.AppendLine("    .stream-name { font-size: 18px; font-weight: 600; color: #333; margin-bottom: 4px; }");
        sb.AppendLine("    .stream-profiles { font-size: 12px; color: #666; }");
        sb.AppendLine("    .profile-badge { display: inline-block; background: #e3f2fd; color: #1976d2; padding: 2px 8px; border-radius: 4px; margin-right: 4px; margin-top: 4px; }");
        sb.AppendLine("    .stream-actions { display: flex; gap: 8px; flex-shrink: 0; }");
        sb.AppendLine("    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; font-weight: 500; display: flex; align-items: center; gap: 6px; transition: background 0.2s; }");
        sb.AppendLine("    .btn-play { background: #0078d4; color: white; }");
        sb.AppendLine("    .btn-play:hover { background: #106ebe; }");
        sb.AppendLine("    .btn-play.playing { background: #d13438; }");
        sb.AppendLine("    .btn-play.playing:hover { background: #e81123; }");
        sb.AppendLine("    .btn-copy { background: #e0e0e0; color: #333; }");
        sb.AppendLine("    .btn-copy:hover { background: #d0d0d0; }");
        sb.AppendLine("    .btn-copy.copied { background: #32cd32; color: white; }");
        sb.AppendLine("    .player-container { margin-top: 16px; display: none; }");
        sb.AppendLine("    .player-container.active { display: block; }");
        sb.AppendLine("    .player-controls { display: flex; align-items: center; gap: 12px; padding: 12px; background: #f0f0f0; border-radius: 8px; }");
        sb.AppendLine("    .quality-select { padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; background: white; font-size: 11px; cursor: pointer; }");
        sb.AppendLine("    .audio-player { display: none; }");
        sb.AppendLine("    .live-indicator { display: flex; align-items: center; gap: 6px; padding: 6px 12px; background: #d13438; color: white; border-radius: 4px; font-size: 12px; font-weight: 600; }");
        sb.AppendLine("    .live-dot { width: 8px; height: 8px; background: white; border-radius: 50%; animation: pulse 1.5s infinite; }");
        sb.AppendLine("    @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.5; } }");
        sb.AppendLine("    .volume-control { display: flex; align-items: center; gap: 8px; flex: 1; }");
        sb.AppendLine("    .volume-icon { color: #666; font-size: 16px; cursor: pointer; }");
        sb.AppendLine("    .volume-slider { flex: 1; max-width: 120px; height: 4px; -webkit-appearance: none; appearance: none; background: #ddd; border-radius: 2px; outline: none; }");
        sb.AppendLine("    .volume-slider::-webkit-slider-thumb { -webkit-appearance: none; appearance: none; width: 14px; height: 14px; background: #0078d4; border-radius: 50%; cursor: pointer; }");
        sb.AppendLine("    .volume-slider::-moz-range-thumb { width: 14px; height: 14px; background: #0078d4; border-radius: 50%; cursor: pointer; border: none; }");
        sb.AppendLine("    .quality-label { font-size: 11px; color: #666; margin-right: 4px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"streams\">");

        var streamIndex = 0;
        foreach (var stream in _streams.Where(s => s.IsEnabled))
        {
            var streamUrl = $"{baseUrl}/hls/{stream.StreamPath}/master.m3u8";
            var logoHtml = !string.IsNullOrEmpty(stream.LogoPath) && File.Exists(stream.LogoPath)
                ? $"<img src=\"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(stream.LogoPath))}\" alt=\"{System.Net.WebUtility.HtmlEncode(stream.Name)}\">"
                : "&#9835;";

            sb.AppendLine($"    <div class=\"stream\" data-url=\"{streamUrl}\" data-index=\"{streamIndex}\">");
            sb.AppendLine($"      <div class=\"stream-header\">");
            sb.AppendLine($"        <div class=\"stream-logo\">{logoHtml}</div>");
            sb.AppendLine($"        <div class=\"stream-info\">");
            sb.AppendLine($"          <div class=\"stream-name\">{System.Net.WebUtility.HtmlEncode(stream.Name)}</div>");
            sb.AppendLine($"          <div class=\"stream-profiles\">");

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
            sb.AppendLine($"          <div class=\"volume-control\">");
            sb.AppendLine($"            <span class=\"volume-icon\" id=\"volume-icon-{streamIndex}\" onclick=\"toggleMute({streamIndex})\">&#128266;</span>");
            sb.AppendLine($"            <input type=\"range\" class=\"volume-slider\" id=\"volume-{streamIndex}\" min=\"0\" max=\"100\" value=\"80\" oninput=\"setVolume({streamIndex})\">");
            sb.AppendLine($"          </div>");
            sb.AppendLine($"          <span class=\"quality-label\">Quality:</span>");
            sb.AppendLine($"          <select class=\"quality-select\" id=\"quality-{streamIndex}\" onchange=\"changeQuality({streamIndex})\"></select>");
            sb.AppendLine($"        </div>");
            sb.AppendLine($"      </div>");
            sb.AppendLine($"    </div>");
            streamIndex++;
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine("    const players = {};");
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
        sb.AppendLine("        if (players[index]) {");
        sb.AppendLine("          players[index].destroy();");
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
        sb.AppendLine("        playerContainer.classList.add('active');");
        sb.AppendLine("        btn.classList.add('playing');");
        sb.AppendLine("        btnText.textContent = 'Stop';");
        sb.AppendLine("        btnIcon.innerHTML = '&#9632;';");
        sb.AppendLine("        initPlayer(index, url);");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function initPlayer(index, url) {");
        sb.AppendLine("      const audio = document.getElementById(`audio-${index}`);");
        sb.AppendLine("      const qualitySelect = document.getElementById(`quality-${index}`);");
        sb.AppendLine("      const volumeSlider = document.getElementById(`volume-${index}`);");
        sb.AppendLine("      ");
        sb.AppendLine("      // Set initial volume");
        sb.AppendLine("      audio.volume = parseFloat(volumeSlider.value) / 100;");
        sb.AppendLine("      ");
        sb.AppendLine("      if (Hls.isSupported()) {");
        sb.AppendLine("        const hls = new Hls({");
        sb.AppendLine("          enableWorker: true,");
        sb.AppendLine("          lowLatencyMode: true,");
        sb.AppendLine("          liveDurationInfinity: true,");
        sb.AppendLine("          liveBackBufferLength: 30,");
        sb.AppendLine("          backBufferLength: 90");
        sb.AppendLine("        });");
        sb.AppendLine("        ");
        sb.AppendLine("        hls.loadSource(url);");
        sb.AppendLine("        hls.attachMedia(audio);");
        sb.AppendLine("        ");
        sb.AppendLine("        hls.on(Hls.Events.MANIFEST_PARSED, function(event, data) {");
        sb.AppendLine("          // Populate quality selector");
        sb.AppendLine("          qualitySelect.innerHTML = '<option value=\"-1\">Auto</option>';");
        sb.AppendLine("          data.levels.forEach((level, i) => {");
        sb.AppendLine("            const bitrate = Math.round(level.bitrate / 1000);");
        sb.AppendLine("            qualitySelect.innerHTML += `<option value=\"${i}\">${bitrate}kbps</option>`;");
        sb.AppendLine("          });");
        sb.AppendLine("          audio.play().catch(e => console.log('Autoplay prevented:', e));");
        sb.AppendLine("        });");
        sb.AppendLine("        ");
        sb.AppendLine("        hls.on(Hls.Events.LEVEL_SWITCHED, function(event, data) {");
        sb.AppendLine("          // Update quality selector when auto-switching");
        sb.AppendLine("          if (hls.autoLevelEnabled) {");
        sb.AppendLine("            qualitySelect.value = '-1';");
        sb.AppendLine("          }");
        sb.AppendLine("        });");
        sb.AppendLine("        ");
        sb.AppendLine("        hls.on(Hls.Events.ERROR, function(event, data) {");
        sb.AppendLine("          console.error('HLS error:', data);");
        sb.AppendLine("          if (data.fatal) {");
        sb.AppendLine("            switch(data.type) {");
        sb.AppendLine("              case Hls.ErrorTypes.NETWORK_ERROR:");
        sb.AppendLine("                hls.startLoad();");
        sb.AppendLine("                break;");
        sb.AppendLine("              case Hls.ErrorTypes.MEDIA_ERROR:");
        sb.AppendLine("                hls.recoverMediaError();");
        sb.AppendLine("                break;");
        sb.AppendLine("              default:");
        sb.AppendLine("                togglePlay(index);");
        sb.AppendLine("                break;");
        sb.AppendLine("            }");
        sb.AppendLine("          }");
        sb.AppendLine("        });");
        sb.AppendLine("        ");
        sb.AppendLine("        players[index] = hls;");
        sb.AppendLine("      } else if (audio.canPlayType('application/vnd.apple.mpegurl')) {");
        sb.AppendLine("        // Native HLS support (Safari)");
        sb.AppendLine("        audio.src = url;");
        sb.AppendLine("        audio.addEventListener('loadedmetadata', function() {");
        sb.AppendLine("          audio.play().catch(e => console.log('Autoplay prevented:', e));");
        sb.AppendLine("        });");
        sb.AppendLine("        qualitySelect.innerHTML = '<option value=\"-1\">Auto</option>';");
        sb.AppendLine("        qualitySelect.disabled = true;");
        sb.AppendLine("      } else {");
        sb.AppendLine("        alert('HLS is not supported in this browser');");
        sb.AppendLine("        togglePlay(index);");
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
        sb.AppendLine("    ");
        sb.AppendLine("    function setVolume(index) {");
        sb.AppendLine("      const audio = document.getElementById(`audio-${index}`);");
        sb.AppendLine("      const slider = document.getElementById(`volume-${index}`);");
        sb.AppendLine("      const icon = document.getElementById(`volume-icon-${index}`);");
        sb.AppendLine("      const volume = parseFloat(slider.value) / 100;");
        sb.AppendLine("      audio.volume = volume;");
        sb.AppendLine("      audio.muted = false;");
        sb.AppendLine("      updateVolumeIcon(icon, volume, false);");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function toggleMute(index) {");
        sb.AppendLine("      const audio = document.getElementById(`audio-${index}`);");
        sb.AppendLine("      const icon = document.getElementById(`volume-icon-${index}`);");
        sb.AppendLine("      audio.muted = !audio.muted;");
        sb.AppendLine("      updateVolumeIcon(icon, audio.volume, audio.muted);");
        sb.AppendLine("    }");
        sb.AppendLine("    ");
        sb.AppendLine("    function updateVolumeIcon(icon, volume, muted) {");
        sb.AppendLine("      if (muted || volume === 0) {");
        sb.AppendLine("        icon.innerHTML = '&#128264;';");
        sb.AppendLine("      } else if (volume < 0.5) {");
        sb.AppendLine("        icon.innerHTML = '&#128265;';");
        sb.AppendLine("      } else {");
        sb.AppendLine("        icon.innerHTML = '&#128266;';");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
