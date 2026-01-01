using System.IO;
using Microsoft.AspNetCore.StaticFiles;

namespace AudioProcessorAndStreamer.Services.Web;

public class HlsContentTypeProvider : IContentTypeProvider
{
    private readonly Dictionary<string, string> _mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // HLS
        { ".m3u8", "application/vnd.apple.mpegurl" },
        { ".m3u", "audio/mpegurl" },
        { ".ts", "video/mp2t" },
        // DASH
        { ".mpd", "application/dash+xml" },
        // fMP4 segments (used by both HLS-fMP4 and DASH)
        { ".m4s", "audio/mp4" },
        { ".mp4", "audio/mp4" },
        // Audio formats
        { ".aac", "audio/aac" },
        { ".mp3", "audio/mpeg" },
        // Images
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".ico", "image/x-icon" }
    };

    public bool TryGetContentType(string subpath, out string contentType)
    {
        var extension = Path.GetExtension(subpath);
        return _mappings.TryGetValue(extension, out contentType!);
    }
}
