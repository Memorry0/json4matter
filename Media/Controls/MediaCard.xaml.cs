using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JsonFormatter.Media.Controls;

/// <summary>点击 URL 后固定的信息卡片：图片/PDF 预览、视频播放、下载。同一时刻只存在一张。</summary>
public partial class MediaCard : UserControl
{
    private JsonNodeVM _node = null!;
    private string _url = "";
    private bool _seeking;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public event EventHandler? Closed;

    public MediaCard()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => TickClock();
        Unloaded += (_, _) => StopAll();
    }

    public void Bind(JsonNodeVM node)
    {
        _node = node;
        _url = node.UrlValue ?? "";
        KindBadge.Text = node.MediaKind switch
        {
            MediaKind.Pdf => "PDF",
            MediaKind.Video => "VIDEO",
            _ => "IMG",
        };
        FileNameText.Text = UrlMedia.FileName(_url);
        MetaText.Text = "";
        DownloadHint.Text = "";
        StateText.Text = "";
        StateText.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewImage.Source = null;
        VideoBar.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Collapsed;

        if (node.MediaKind == MediaKind.Video)
            SetupVideo();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var info = await MediaFetcher.FetchInfoAsync(_url);
        string meta = info.SizeText +
                      (info.LastModified is { } t ? $" · {t:yyyy-MM-dd HH:mm}" : "") +
                      (info.ContentType is { } ct ? $" · {ct}" : "");

        switch (_node.MediaKind)
        {
            case MediaKind.Video:
                MetaText.Text = meta;
                break;

            case MediaKind.Pdf:
            {
                MetaText.Text = meta;
                StateText.Text = "加载中…";
                StateText.Visibility = Visibility.Visible;
                try
                {
                    var bytes = await MediaFetcher.DownloadBytesAsync(_url);
                    var pdf = await Task.Run(() =>
                        MediaCodecs.Pdfium.TryLoad() ? MediaCodecs.RenderPdf(bytes) : null);
                    if (pdf is null)
                    {
                        PreviewImage.Visibility = Visibility.Collapsed;
                        StateText.Text = "PDF 组件不可用（可在设置中重新开启多媒体支持）";
                        return;
                    }
                    PreviewImage.Source = pdf.FirstPage;
                    PreviewImage.MaxHeight = 420;
                    StateText.Visibility = Visibility.Collapsed;
                    MetaText.Text = $"{pdf.PageCount} 页 · {pdf.WidthPt:0}×{pdf.HeightPt:0} pt · " + meta;
                }
                catch (Exception ex)
                {
                    PreviewImage.Visibility = Visibility.Collapsed;
                    StateText.Text = "加载失败：" + ex.Message;
                }
                break;
            }

            default: // 图片
            {
                StateText.Text = "加载中…";
                StateText.Visibility = Visibility.Visible;
                try
                {
                    var bytes = await MediaFetcher.DownloadBytesAsync(_url);
                    var img = await Task.Run(() => MediaCodecs.DecodeImage(bytes, UrlMedia.FileName(_url)));
                    if (img is null)
                    {
                        PreviewImage.Visibility = Visibility.Collapsed;
                        StateText.Text = (MediaCodecs.LastDecodeError is { } err ? "无法解码：" + err : (MediaCodecs.SkiaReady ? "无法解码此图片" : "需要多媒体组件（设置中开启）"));
                        return;
                    }
                    PreviewImage.Source = img.Source;
                    StateText.Visibility = Visibility.Collapsed;
                    MetaText.Text = $"{img.Width} × {img.Height} · " + meta;
                }
                catch (Exception ex)
                {
                    PreviewImage.Visibility = Visibility.Collapsed;
                    StateText.Text = "加载失败：" + ex.Message;
                }
                break;
            }
        }
    }

    // ---------- 视频 ----------

    private void SetupVideo()
    {
        VideoBar.Visibility = Visibility.Visible;
        Player.Visibility = Visibility.Visible;
        PreviewImage.Visibility = Visibility.Collapsed;
        Player.Volume = 0.8;
        StateText.Text = "连接视频…";
        StateText.Visibility = Visibility.Visible;
        Player.Source = new Uri(_url);
        Player.Play();
        _clock.Start();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        StateText.Visibility = Visibility.Collapsed;
        VolumeSlider.Value = Player.Volume;
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StateText.Text = "无法播放（可能缺少系统解码器），可使用下方下载按钮保存后观看";
        StateText.Visibility = Visibility.Visible;
        VideoBar.Visibility = Visibility.Collapsed;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        // 流式/未加载完的源 NaturalDuration 是 Automatic（无 TimeSpan），直接切换播放态即可
        if (PlayButton.Content.ToString() == "▶")
        {
            Player.Play();
            PlayButton.Content = "⏸";
        }
        else
        {
            Player.Pause();
            PlayButton.Content = "▶";
        }
    }

    private void OnMute(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = !Player.IsMuted;
        MuteButton.Content = Player.IsMuted ? "🔇" : "🔊";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => Player.Volume = e.NewValue;

    private void OnSeekStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void OnSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
            Player.Position = TimeSpan.FromMilliseconds(ProgressSlider.Value * Player.NaturalDuration.TimeSpan.TotalMilliseconds);
        _seeking = false;
    }

    private void TickClock()
    {
        if (_seeking || !Player.NaturalDuration.HasTimeSpan) return;
        var total = Player.NaturalDuration.TimeSpan;
        ProgressSlider.Value = total.TotalMilliseconds > 0
            ? Player.Position.TotalMilliseconds / total.TotalMilliseconds
            : 0;
        TimeText.Text = $"{Fmt(Player.Position)} / {Fmt(total)}";

        static string Fmt(TimeSpan t) => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void StopAll()
    {
        _clock.Stop();
        try { Player.Close(); Player.Source = null; } catch { }
    }

    // ---------- 操作 ----------

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        string name = UrlMedia.FileName(_url);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var dlg = new SaveFileDialog
        {
            FileName = string.IsNullOrEmpty(ext) ? name + ".bin" : name,
            Filter = "全部文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        DownloadButton.IsEnabled = false;
        DownloadHint.Text = "下载中…";
        var progress = new Progress<double>(p =>
            DownloadHint.Text = p >= 1 ? "下载完成" : $"下载中… {p:P0}");
        try
        {
            await MediaFetcher.DownloadToFileAsync(_url, dlg.FileName, progress);
        }
        catch (Exception ex)
        {
            DownloadHint.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
