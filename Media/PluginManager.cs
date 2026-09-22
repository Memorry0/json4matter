using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace JsonFormatter.Media;

/// <summary>
/// 插件式依赖按需下载：开启「多媒体支持」后从 NuGet flat container 拉取
/// SkiaSharp（webp）/ Svg.Skia（svg）/ pdfium（PDF 渲染），解压到
/// %APPDATA%/JsonFormatter/plugins 并加载。工具本体保持零依赖小体积。
/// </summary>
public static class PluginManager
{
    // 根包（依赖树运行时按 nuspec 递归解析，无需手工枚举闭包）。
    // 注意：Svg.Skia 5.x 必须配 SkiaSharp 4.x；1.0.0.18 老线会解析出不配套的 Svg.Model。
    private static readonly (string Id, string Version)[] Roots =
    {
        ("SkiaSharp", "4.152.1"),
        ("Svg.Skia", "5.2.3"),
        ("bblanchon.PDFium.Win32", "156.0.8066"),
    };

    private static readonly string ManagedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "plugins", "managed");
    private static readonly string NativeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "plugins", "native");
    private static readonly string PkgDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "packages");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static bool Ready { get; private set; }
    public static bool Busy { get; private set; }
    public static string? Error { get; private set; }

    /// <summary>确保插件可用：本地已就绪直接返回；否则下载并加载。UI 线程外调用。</summary>
    public static async Task EnsureReadyAsync()
    {
        if (Ready || Busy) return;
        Busy = true;
        Error = null;
        try
        {
            if (!File.Exists(Path.Combine(ManagedDir, "SkiaSharp.dll")) ||
                !Directory.Exists(NativeDir) || Directory.GetFiles(NativeDir, "*.dll").Length == 0)
            {
                await DownloadAndExtractAsync();
            }
            LoadAll();
            Ready = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------- 下载与解压 ----------

    private static async Task DownloadAndExtractAsync()
    {
        Directory.CreateDirectory(ManagedDir);
        Directory.CreateDirectory(NativeDir);
        Directory.CreateDirectory(PkgDir);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string Version)>(Roots);
        while (queue.Count > 0)
        {
            var (id, ver) = queue.Dequeue();
            string key = id.ToLowerInvariant() + "/" + ver;
            if (!visited.Add(key)) continue;

            string nupkg = await DownloadPackageAsync(id, ver);
            foreach (var (depId, depRange) in ReadDependencies(nupkg))
            {
                string depVer = await ResolveVersionAsync(depId, depRange);
                if (depVer is not null)
                    queue.Enqueue((depId, depVer));
            }
            Extract(nupkg);
        }
    }

    private static async Task<string> DownloadPackageAsync(string id, string version)
    {
        string file = Path.Combine(PkgDir, $"{id}.{version}.nupkg");
        if (File.Exists(file) && new FileInfo(file).Length > 0)
            return file;
        string url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/{version}/{id.ToLowerInvariant()}.{version}.nupkg";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await src.CopyToAsync(dst);
        return file;
    }

    /// <summary>读 nupkg 内 .nuspec 的依赖组（优先 net6.0/windows，退 netstandard2.0/netcoreapp3.1 组）。</summary>
    private static IEnumerable<(string Id, string Version)> ReadDependencies(string nupkg)
    {
        using var zip = ZipFile.OpenRead(nupkg);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("nupkg 缺少 nuspec");
        using var s = entry.Open();
        var xml = XDocument.Load(s);
        var groups = xml.Descendants()
            .Where(e => e.Name.LocalName == "group")
            .Select(g => new
            {
                Tf = (string?)g.Attribute("targetFramework") ?? "",
                Deps = g.Descendants()
                    .Where(d => d.Name.LocalName == "dependency")
                    .Select(d => ((string?)d.Attribute("id") ?? "", (string?)d.Attribute("version") ?? ""))
                    .ToList(),
            })
            .ToList();

        string[] preferred = { "net6.0", "net6.0-windows", "netstandard2.1", "netstandard2.0", "netcoreapp3.1", "uap10.0" };
        foreach (var p in preferred)
        {
            var g = groups.FirstOrDefault(x => x.Tf.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (g is not null)
                return g.Deps.Where(d => d.Item1.Length > 0);
        }
        return Enumerable.Empty<(string, string)>();
    }

    /// <summary>把 nuspec 版本范围（如 [2.88.3, 3.0) / ≥2.x）解析为 flatcontainer 上可用的具体版本。</summary>
    private static async Task<string?> ResolveVersionAsync(string id, string range)
    {
        // 取下限
        string lower = range.Trim('[', '(', ')').Split(',')[0].Trim().TrimStart('=', ' ');
        bool lowerValid = Version.TryParse(lower.Contains('*') ? lower.Replace(".*", "") : lower, out _);

        string url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json";
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        if (!doc.RootElement.TryGetProperty("versions", out var versions))
            return null;

        string? best = null;
        int lowerMajor = lower.Split('.')[0].TrimStart('v') is { Length: > 0 } mj && int.TryParse(mj, out var m) ? m : -1;
        foreach (var v in versions.EnumerateArray())
        {
            string s = v.GetString() ?? "";
            if (s.Contains('-')) continue; // 跳过预发布
            if (lowerValid && string.CompareOrdinal(Normalize(s), Normalize(lower)) < 0) continue;
            // 与下限保持同一主版本，避免 2.88.x 的依赖被解析到 4.x 大版本（API 不兼容）
            if (lowerMajor >= 0 && (s.Split('.')[0].TrimStart('v') is { Length: > 0 } smj && int.TryParse(smj, out var sm) ? sm : -1) != lowerMajor)
                continue;
            if (best is null || string.CompareOrdinal(Normalize(s), Normalize(best)) > 0)
                best = s;
        }
        // 找不到满足下限的就用最新版兜底
        if (best is null)
            foreach (var v in versions.EnumerateArray())
            {
                string s = v.GetString() ?? "";
                if (!s.Contains('-') && (best is null || string.CompareOrdinal(Normalize(s), Normalize(best)) > 0))
                    best = s;
            }
        return best;

        static string Normalize(string v)
        {
            var parts = v.Split('.');
            while (parts.Length < 3)
                parts = parts.Append("0").ToArray();
            return string.Join(".", parts.Take(parts.Length >= 3 ? 3 : parts.Length).Concat(parts.Skip(3)));
        }
    }

    private static void Extract(string nupkg)
    {
        using var zip = ZipFile.OpenRead(nupkg);
        foreach (var e in zip.Entries)
        {
            string name = e.FullName;
            // 托管库：取最高兼容 TFM 的 lib 目录
            if (name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll"))
            {
                string tfm = name.Split('/')[1];
                if (tfm is "net6.0" or "net6.0-windows" or "netstandard2.1" or "netstandard2.0"
                    or "netcoreapp3.1" or "net461" or "net48")
                {
                    string dest = Path.Combine(ManagedDir, Path.GetFileName(name));
                    ExtractEntry(e, dest, keepNewerTfm: tfm);
                }
            }
            else if (name.StartsWith("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase)
                     && name.EndsWith(".dll"))
            {
                ExtractEntry(e, Path.Combine(NativeDir, Path.GetFileName(name)), keepNewerTfm: null);
            }
            // PdfiumViewer.Native 系列包把 pdfium.dll 放在 Build/x64/
            else if (name.StartsWith("Build/x64/", StringComparison.OrdinalIgnoreCase)
                     && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                ExtractEntry(e, Path.Combine(NativeDir, Path.GetFileName(name)), keepNewerTfm: null);
            }
        }
    }

    private static readonly Dictionary<string, int> TfmRank = new()
    {
        ["net461"] = 1, ["net48"] = 2, ["netstandard2.0"] = 3, ["netstandard2.1"] = 4,
        ["netcoreapp3.1"] = 5, ["net6.0"] = 6, ["net6.0-windows"] = 7,
    };

    private static void ExtractEntry(ZipArchiveEntry entry, string dest, string? keepNewerTfm)
    {
        // 同名文件按 TFM 等级覆盖（只保留最高兼容版本）
        if (File.Exists(dest))
        {
            if (keepNewerTfm is null) return; // native 已有
            string existingTfm = File.ReadAllText(dest + ".tfm").Trim();
            if (TfmRank.GetValueOrDefault(existingTfm, 0) >= TfmRank.GetValueOrDefault(keepNewerTfm, 0))
                return;
        }
        entry.ExtractToFile(dest, overwrite: true);
        if (keepNewerTfm is not null)
            File.WriteAllText(dest + ".tfm", keepNewerTfm);
    }

    // ---------- 加载 ----------

    private static bool _resolverHooked;
    private static readonly List<Assembly> _loaded = new();

    private static void LoadAll()
    {
        if (!_resolverHooked)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string simple = new AssemblyName(e.Name).Name;
                string path = Path.Combine(ManagedDir, simple + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            // native 搜索目录
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + NativeDir;
            Environment.SetEnvironmentVariable("PATH", dirs);
            foreach (var dll in Directory.GetFiles(NativeDir, "*.dll"))
            {
                try { NativeLibrary.Load(dll); } catch { /* 由具体调用报错 */ }
            }
            _resolverHooked = true;
        }

        // SkiaSharp 必须最先（Svg.Skia 引用它）
        string skia = Path.Combine(ManagedDir, "SkiaSharp.dll");
        if (File.Exists(skia))
            _loaded.Add(Assembly.LoadFrom(skia));
        foreach (var dll in Directory.GetFiles(ManagedDir, "*.dll"))
        {
            if (dll.EndsWith("SkiaSharp.dll", StringComparison.OrdinalIgnoreCase)) continue;
            try { _loaded.Add(Assembly.LoadFrom(dll)); } catch { /* 元数据冲突等可忽略 */ }
        }
    }
}
