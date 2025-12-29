using System.IO;
using Microsoft.AspNetCore.StaticFiles;

namespace AudioProcessorAndStreamer.Services.Web;

public class HlsContentTypeProvider : IContentTypeProvider
{
    private readonly Dictionary<string, string> _mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".m3u8", "application/vnd.apple.mpegurl" },
        { ".m3u", "audio/mpegurl" },
        { ".ts", "video/mp2t" },
        { ".aac", "audio/aac" },
        { ".mp3", "audio/mpeg" },
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
