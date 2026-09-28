using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JsonFormatter.Media.Controls;

/// <summary>树内媒体 URL 值：悬浮 300ms 出预览，点击弹固定卡片（由主窗口承载）。</summary>
public partial class MediaValueControl : UserControl
{
    private readonly DispatcherTimer _hoverDelay;
    /// <summary>鼠标离开 URL/悬浮层后的延迟关闭（StaysOpen 的 Popup 需手动管理，且防鼠标在两者间隙抖动时闪烁）。</summary>
    private readonly DispatcherTimer _leaveDelay;
    private JsonNodeVM? Node => DataContext as JsonNodeVM;
    private bool _loading;
    private bool _suppressHover;

    public static event EventHandler<JsonNodeVM>? MediaClicked;

    public MediaValueControl()
    {
        InitializeComponent();
        _hoverDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverDelay.Tick += (_, _) => { _hoverDelay.Stop(); OpenHover(); };
        _leaveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _leaveDelay.Tick += (_, _) => { _leaveDelay.Stop(); CloseHover(); };
        Unloaded += (_, _) => { _hoverDelay.Stop(); _leaveDelay.Stop(); Hover.IsOpen = false; };
    }

    /// <summary>暴露 InvokePattern：UIA/辅助工具可直接“点击”媒体值（也方便自动化测试）。</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new MediaValuePeer(this);

    private sealed class MediaValuePeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer,
        System.Windows.Automation.Provider.IInvokeProvider
    {
        public MediaValuePeer(MediaValueControl owner) : base(owner) { }

        private MediaValueControl Src => (MediaValueControl)Owner;

        public override object GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface)
            => patternInterface == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : null;

        public void Invoke()
        {
            if (Src.Node is { UrlValue: not null } node)
                MediaClicked?.Invoke(Src, node);
        }

        protected override string GetNameCore()
            => Src.Node?.SlotText ?? base.GetNameCore();

        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.Hyperlink;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _leaveDelay.Stop();
        if (Node?.UrlValue is null || _suppressHover) return;
        _hoverDelay.Stop();
        _hoverDelay.Start();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverDelay.Stop();
        _leaveDelay.Stop();
        _leaveDelay.Start();
    }

    /// <summary>鼠标进入悬浮层：取消关闭。</summary>
    private void OnHoverEnter(object sender, MouseEventArgs e) => _leaveDelay.Stop();

    /// <summary>鼠标离开悬浮层：延迟关闭（给移回 URL 的机会）。</summary>
    private void OnHoverLeave(object sender, MouseEventArgs e)
    {
        _leaveDelay.Stop();
        _leaveDelay.Start();
    }

    private void CloseHover()
    {
        Hover.IsOpen = false;
        _suppressHover = false;
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (Node?.UrlValue is null) return;
        _hoverDelay.Stop();
        _leaveDelay.Stop();
        _suppressHover = true; // 点击后抑制悬浮，直到鼠标移走
        MediaClicked?.Invoke(this, Node);
        e.Handled = true;
    }

    private void OpenHover()
    {
        if (Node is not { UrlValue: { } url, MediaKind: { } kind } || kind == MediaKind.None)
            return;
        Hover.IsOpen = true;

        // pdf / 视频：悬浮只显示基本信息
        if (kind != MediaKind.Image)
        {
            HoverImage.Visibility = Visibility.Collapsed;
            HoverState.Text = "加载中…";
            HoverTitle.Text = UrlMedia.FileName(url);
            _ = LoadInfoAsync(url);
            return;
        }

        HoverImage.Visibility = Visibility.Visible;
        HoverState.Text = "加载中…";
        _ = LoadImageAsync(url);
    }

    private async Task LoadInfoAsync(string url)
    {
        var info = await MediaFetcher.FetchInfoAsync(url);
        if (!Hover.IsOpen) return;
        string kindText = Node!.MediaKind == MediaKind.Pdf ? "PDF 文档" : "视频文件";
        HoverMeta.Text = $"{kindText} · {info.SizeText}" +
                         (info.LastModified is { } t ? $" · {t:yyyy-MM-dd HH:mm}" : "");
        HoverState.Text = "点击查看详情";
    }

    private async Task LoadImageAsync(string url)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var info = await MediaFetcher.FetchInfoAsync(url);
            var bytes = await MediaFetcher.DownloadBytesAsync(url);
            var img = await Task.Run(() => MediaCodecs.DecodeImage(bytes, UrlMedia.FileName(url)));
            if (!Hover.IsOpen) return;
            if (img is null)
            {
                HoverImage.Source = null;
                HoverState.Text = MediaCodecs.SkiaReady ? "无法解码此图片" : "需要多媒体组件（设置中开启）";
                return;
            }
            HoverImage.Source = img.Source;
            HoverTitle.Text = UrlMedia.FileName(url);
            HoverMeta.Text = $"{img.Width} × {img.Height} · {info.SizeText}" +
                             (info.LastModified is { } t ? $" · {t:yyyy-MM-dd HH:mm}" : "");
            HoverState.Text = "点击查看大图";
        }
        catch (Exception ex)
        {
            if (Hover.IsOpen)
                HoverState.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }
}
