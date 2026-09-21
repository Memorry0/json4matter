using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace JsonFormatter;

/// <summary>当前主题的树节点配色（值按类型着色、key 近黑、修改红、选中底色）。</summary>
public sealed record ThemePalette(
    IBrush Key, IBrush IndexKey, IBrush RootKey,
    IBrush String, IBrush Number, IBrush Bool, IBrush Null,
    IBrush Count, IBrush Modified,
    IBrush SubtreeBg, IBrush SelBg)
{
    public static readonly ThemePalette Green = new(
        Brush("#1E2A23"), Brush("#566E7C"), Brush("#1E2A23"),
        Brush("#0B7A5C"), Brush("#2456B8"), Brush("#B26205"), Brush("#6E7B76"),
        Brush("#0B7A5C"), Brush("#C13A2F"),
        Brush("#E9F5EF"), Brush("#C8E9D8"));

    public static readonly ThemePalette Paper = new(
        Brush("#33291A"), Brush("#77664A"), Brush("#33291A"),
        Brush("#7A4B12"), Brush("#274F8F"), Brush("#8F4A08"), Brush("#7A7261"),
        Brush("#7A4B12"), Brush("#B3261E"),
        Brush("#F5EDD9"), Brush("#EBDDB6"));

    public static readonly ThemePalette White = new(
        Brush("#16181D"), Brush("#5A6472"), Brush("#16181D"),
        Brush("#0F766E"), Brush("#1D4ED8"), Brush("#B45309"), Brush("#6B7280"),
        Brush("#0F766E"), Brush("#B91C1C"),
        Brush("#ECF2FD"), Brush("#D7E4FB"));

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));}

/// <summary>主题切换：换 chrome 资源字典 + 换 VM 配色。</summary>
public static class ThemeManager
{
    public static ThemePalette Current { get; private set; } = ThemePalette.Green;

    public static string CurrentId { get; private set; } = "Green";

    /// <summary>界面（非等宽）字体：mac 用苹方，win 用雅黑。</summary>
    public static string UiFontFamily =>
        OperatingSystem.IsMacOS() ? "PingFang SC"
        : OperatingSystem.IsWindows() ? "Microsoft YaHei UI"
        : "Inter";

    /// <summary>代码等宽字体。</summary>
    public static string MonoFontFamily =>
        OperatingSystem.IsMacOS() ? "Menlo" : "Consolas";

    public static void Apply(string id, bool applyResources)
    {
        CurrentId = id switch
        {
            "Paper" => "Paper",
            "White" => "White",
            _ => "Green",
        };
        Current = CurrentId switch
        {
            "Paper" => ThemePalette.Paper,
            "White" => ThemePalette.White,
            _ => ThemePalette.Green,
        };

        if (applyResources)
        {
            var dict = (Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri($"avares://JsonFormatter/Themes/Theme{CurrentId}.axaml"));
            var merged = ((Avalonia.Controls.ResourceDictionary)App.Current.Resources).MergedDictionaries;
            if (merged.Count > 0)
                merged[merged.Count - 1] = dict;
            else
                merged.Add(dict);
        }
    }
}
