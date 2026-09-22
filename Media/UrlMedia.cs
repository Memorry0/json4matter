using System.Text.RegularExpressions;

namespace JsonFormatter.Media;

public enum MediaKind
{
    None,
    Image,
    Pdf,
    Video,
}

/// <summary>字符串值是否为可预览的媒体 URL（http/https + 常见扩展名，带 query/hash 也认）。</summary>
public static partial class UrlMedia
{
    [GeneratedRegex(
        @"^https?://[^\s""']+?\.(jpg|jpeg|png|webp|gif|bmp|svg|heic|heif|pdf|mp4|avi|mkv|mov|wmv|flv|webm)([?#][^\s]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static MediaKind Detect(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048)
            return MediaKind.None;
        var m = Pattern().Match(url);
        if (!m.Success)
            return MediaKind.None;
        return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "pdf" => MediaKind.Pdf,
            "mp4" or "avi" or "mkv" or "mov" or "wmv" or "flv" or "webm" => MediaKind.Video,
            _ => MediaKind.Image,
        };
    }

    /// <summary>URL 去掉 query/hash 后的文件名（含扩展名），取不到则用整段。</summary>
    public static string FileName(string url)
    {
        int cut = url.IndexOfAny(new[] { '?', '#' });
        string path = cut < 0 ? url : url[..cut];
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}

/// <summary>多媒体功能总开关（设置开关 + 插件就绪后置 true；树构建时读取）。</summary>
public static class MediaSupport
{
    /// <summary>用户设置里的开关（立即生效：基础格式 jpg/png/gif/bmp/heic 与视频不需要插件）。</summary>
    public static volatile bool Enabled;
}
