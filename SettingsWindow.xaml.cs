using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JsonFormatter;

public partial class SettingsWindow : Window
{
    public event Action<string>? ThemeChanged;
    public event Action<double>? FontSizeChanged;

    public SettingsWindow()
    {
        InitializeComponent();

        FontSizeSlider.Value = Settings.Data.FontSize;
        FontSizeLabel.Text = $"{Settings.Data.FontSize:0}";
        FullscreenSwitch.IsChecked = Settings.Data.DefaultFullscreen;
        MediaSwitch.IsChecked = Settings.Data.EnableMedia;

        MarkSelected(Settings.Data.Theme);
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double v = System.Math.Round(e.NewValue);
        FontSizeLabel.Text = $"{v:0}";
        if (System.Math.Abs(v - Settings.Data.FontSize) > 0.01)
            FontSizeChanged?.Invoke(v);
    }

    private void OnFullscreenChanged(object sender, RoutedEventArgs e)
    {
        Settings.Data.DefaultFullscreen = FullscreenSwitch.IsChecked == true;
        Settings.Save();
    }

    public event Action<bool>? MediaToggled;

    private void OnMediaChanged(object sender, RoutedEventArgs e)
    {
        bool on = MediaSwitch.IsChecked == true;
        Settings.Data.EnableMedia = on;
        Settings.Save();
        MediaToggled?.Invoke(on);
    }

    private void OnHelpClicked(object sender, RoutedEventArgs e)
    {
        var w = new Window
        {
            Title = "关于多媒体支持",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            Background = (Brush)TryFindResource("BgCanvas") ?? Brushes.White,
            FontFamily = FontFamily,
        };
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        sp.Children.Add(new TextBlock
        {
            Text = "多媒体支持是什么？",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)TryFindResource("InkCaption")!,
            Margin = new Thickness(0, 0, 0, 10),
        });
        string body =
            "开启后，当 JSON 值是 http/https 开头的媒体链接时：\n" +
            "· 图片（jpg/png/webp/gif/bmp/svg/heic 等）：鼠标悬浮显示预览图，点击查看大图与宽高、大小、时间、文件名，可下载\n" +
            "· PDF：悬浮显示基本信息，点击渲染首页并可下载\n" +
            "· 视频（mp4/avi 等）：悬浮显示基本信息，点击后在卡片内直接播放，可下载\n\n" +
            "为控制工具体积，webp/svg/PDF 的解码组件（约 20MB）不会内置在程序里，首次开启时会联网从 NuGet 下载到本地缓存，之后离线可用。关闭开关即停用识别（已下载的组件保留）。\n\n" +
            "提示：GIF 显示第一帧；HEIC 依赖系统 HEIF 扩展；avi/mkv 播放取决于系统解码器，无法播放时可下载后观看。";
        sp.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 20,
            Foreground = (Brush)TryFindResource("InkCaption")!,
        });
        var ok = new Button
        {
            Content = "知道了",
            Width = 88,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Background = (Brush)TryFindResource("Accent"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        ok.Click += (_, _) => w.Close();
        sp.Children.Add(ok);
        w.Content = sp;
        w.ShowDialog();
    }

    private void OnThemeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ThemeChanged?.Invoke(id);
            MarkSelected(id);
        }
    }

    private void MarkSelected(string id)
    {
        Select(CardGreen, id == "Green");
        Select(CardPaper, id == "Paper");
        Select(CardWhite, id == "White");
    }

    private static void Select(Button card, bool selected)
    {
        card.BorderBrush = selected
            ? (Brush)Application.Current.TryFindResource("Accent")
            : (Brush)Application.Current.TryFindResource("LineStrong");
        card.Background = selected
            ? (Brush)Application.Current.TryFindResource("AccentSoft")
            : (Brush)Application.Current.TryFindResource("BgSurface");
        card.BorderThickness = new Thickness(selected ? 2 : 1.5);
    }
}
