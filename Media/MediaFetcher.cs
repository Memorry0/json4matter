using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace JsonFormatter.Media;

public sealed record MediaInfo(long? SizeBytes, string? ContentType, DateTime? LastModified)
{
    public string SizeText => SizeBytes is not { } b ? "大小未知" : FormatSize(b);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}

/// <summary>HTTP 信息获取 / 下载 / 简单磁盘缓存。单例 HttpClient。</summary>
public static class MediaFetcher
{
    private const long MaxBytes = 64 * 1024 * 1024; // 预览下载上限 64MB

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "cache");

    static MediaFetcher()
    {
        try { Http.DefaultRequestHeaders.UserAgent.ParseAdd("JsonFormatter/1.0 (desktop)"); }
        catch { }
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>HEAD 拿大小/类型/时间；服务器不支持时字段为空。</summary>
    public static async Task<MediaInfo> FetchInfoAsync(string url)
    {
        try
        {
            using var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            resp.EnsureSuccessStatusCode();
            return new MediaInfo(
                resp.Content.Headers.ContentLength,
                resp.Content.Headers.ContentType?.MediaType,
                resp.Content.Headers.LastModified?.UtcDateTime.ToLocalTime());
        }
        catch
        {
            return new MediaInfo(null, null, null);
        }
    }

    private static string CachePathOf(string url)
    {
        using var sha = SHA1.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url)));
        string ext = Path.GetExtension(UrlMedia.FileName(url));
        return Path.Combine(CacheDir, hash + ext);
    }

    /// <summary>下载字节（优先磁盘缓存）。超上限抛 InvalidOperationException。</summary>
    public static async Task<byte[]> DownloadBytesAsync(string url)
    {
        string cache = CachePathOf(url);
        if (File.Exists(cache) && new FileInfo(cache).Length > 0)
            return await File.ReadAllBytesAsync(cache);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? len = resp.Content.Headers.ContentLength;
        if (len is > MaxBytes)
            throw new InvalidOperationException($"文件 {MediaInfo.FormatSize(len.Value)} 超过预览上限 64 MB");

        await using var src = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream(len > 0 ? (int)Math.Min(len.Value, int.MaxValue) : 64 * 1024);
        await src.CopyToAsync(ms);
        byte[] bytes = ms.ToArray();
        try { await File.WriteAllBytesAsync(cache, bytes); } catch { /* 缓存失败不阻塞 */ }
        return bytes;
    }

    /// <summary>下载到用户指定文件（下载按钮），报告进度 0..1。</summary>
    public static async Task DownloadToFileAsync(string url, string destFile, IProgress<double>? progress = null)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            read += n;
            if (total > 0)
                progress?.Report((double)read / total);
        }
        progress?.Report(1);
    }
}
