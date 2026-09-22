using System.IO;
using System.Text.Json;

namespace JsonFormatter;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Green";
    public double FontSize { get; set; } = 13;
    public bool DefaultFullscreen { get; set; }
    public bool EnableMedia { get; set; }
}

public static class Settings
{
    public static AppSettings Data { get; private set; } = new();

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(Path))
                Data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch { /* 坏配置则回到默认 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 保存失败不阻塞 UI */ }
    }
}
