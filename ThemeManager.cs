using System.Windows;
using System.Windows.Media;

namespace JsonFormatter;

/// <summary>当前主题的树节点配色（key 近黑、值按类型分色、修改红、选中底色）。</summary>
public sealed record ThemePalette(
    Brush Key, Brush IndexKey, Brush RootKey,
    Brush String, Brush Number, Brush Bool, Brush Null, Brush Empty,
    Brush Count, Brush Modified,
    Brush SubtreeBg, Brush SelBg)
{
    public static readonly ThemePalette Green = new(
        B("#1E2A23"), B("#566E7C"), B("#1E2A23"),
        B("#0B7A5C"), B("#2456B8"), B("#B26205"), B("#6E7B76"), B("#6E7B76"),
        B("#0B7A5C"), B("#C13A2F"),
        B("#E9F5EF"), B("#C8E9D8"));

    public static readonly ThemePalette Paper = new(
        B("#33291A"), B("#77664A"), B("#33291A"),
        B("#7A4B12"), B("#274F8F"), B("#8F4A08"), B("#7A7261"), B("#7A7261"),
        B("#7A4B12"), B("#B3261E"),
        B("#F5EDD9"), B("#EBDDB6"));

    public static readonly ThemePalette White = new(
        B("#16181D"), B("#5A6472"), B("#16181D"),
        B("#0F766E"), B("#1D4ED8"), B("#B45309"), B("#6B7280"), B("#6B7280"),
        B("#0F766E"), B("#B91C1C"),
        B("#ECF2FD"), B("#D7E4FB"));

    private static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>主题切换：换 chrome 资源字典 + 换 VM 配色。</summary>
public static class ThemeManager
{
    public static ThemePalette Current { get; private set; } = ThemePalette.Green;
    public static string CurrentId { get; private set; } = "Green";

    public static void Apply(string id)
    {
        CurrentId = id switch { "Paper" => "Paper", "White" => "White", _ => "Green" };
        Current = CurrentId switch
        {
            "Paper" => ThemePalette.Paper,
            "White" => ThemePalette.White,
            _ => ThemePalette.Green,
        };

        var uri = new Uri($"Themes/Theme{CurrentId}.xaml", UriKind.Relative);
        var dict = (ResourceDictionary)Application.LoadComponent(uri);
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Add(dict);
    }
}
