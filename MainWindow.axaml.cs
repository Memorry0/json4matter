using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace JsonFormatter;

public partial class MainWindow : Window
{
    private const string SampleJson = """
        {
          "code": 200,
          "message": "查询成功",
          "success": true,
          "data": {
            "schoolName": "阳光实验中学",
            "schoolCode": "YGSY-2026-001",
            "address": "海淀区学院路88号",
            "totalTeachers": 126,
            "totalStudents": 1860,
            "hasBoarding": true,
            "remark": null,
            "tags": [],
            "gradeList": [
              {
                "gradeId": 7,
                "gradeName": "初一年级",
                "headTeacher": "王建国",
                "studentCount": 620,
                "classList": [
                  {"classId": "C0701", "className": "初一（1）班", "teacher": "李小红", "studentCount": 45, "subjects": ["语文", "数学", "英语", "生物"]},
                  {"classId": "C0702", "className": "初一（2）班", "teacher": "赵强", "studentCount": 44, "subjects": ["语文", "数学", "英语"]},
                  {"classId": "C0703", "className": "初一（3）班", "teacher": "陈敏", "studentCount": 46, "subjects": ["语文", "数学", "英语", "地理"]}
                ]
              },
              {
                "gradeId": 8,
                "gradeName": "初二年级",
                "headTeacher": "刘芳",
                "studentCount": 640,
                "classList": [
                  {"classId": "C0801", "className": "初二（1）班", "teacher": "周伟", "studentCount": 47, "subjects": ["语文", "数学", "英语", "物理"]},
                  {"classId": "C0802", "className": "初二（2）班", "teacher": "吴磊", "studentCount": 43, "subjects": ["语文", "数学", "英语", "物理"]}
                ]
              },
              {
                "gradeId": 9,
                "gradeName": "初三年级",
                "headTeacher": "孙丽",
                "studentCount": 600,
                "classList": []
              }
            ]
          }
        }
        """;

    private readonly DispatcherTimer _debounce;
    private int _seq;
    private JsonDocument? _doc;
    private string? _pretty;
    private JsonNodeVM? _root;
    private string? _parsedText;
    private JsonNodeVM.BaselineInfo? _prevBaseline;
    private bool _freshContent;
    private bool _hideEmpty;

    private static readonly JsonSerializerOptions IndentOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions CompactRawOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions CompactEscapeOptions = new();

    public MainWindow()
    {
        InitializeComponent();

        FontFamily = new FontFamily(ThemeManager.UiFontFamily);
        this.Resources["MonoFont"] = new FontFamily(ThemeManager.MonoFontFamily);
        InputBox.FontFamily = new FontFamily(ThemeManager.MonoFontFamily);
        ResultTree.FontFamily = new FontFamily(ThemeManager.MonoFontFamily);

        ApplyFontSize(Settings.Data.FontSize);
        if (Settings.Data.DefaultFullscreen)
            WindowState = WindowState.Maximized;

        Placeholder.IsVisible = true;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunParse(); };

        // 单击定位：光标落点 → 右树定位（TextBox 内部先处理光标）
        InputBox.AddHandler(InputElement.PointerReleasedEvent, OnInputPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        BuildTreeMenu();
        InputBox.Focus();
    }

    // ---------- 解析 ----------

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        Placeholder.IsVisible = string.IsNullOrEmpty(InputBox.Text);
        _debounce.Stop();
        _debounce.Start();
    }

    private async void RunParse()
    {
        int seq = ++_seq;
        string text = InputBox.Text ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            _doc?.Dispose();
            _doc = null; _pretty = null; _root = null; _parsedText = null; _prevBaseline = null;
            ResultTree.ItemsSource = null;
            EmptyHint.IsVisible = true;
            SizeText.Text = "";
            SetStatus(Neutral(), "就绪 — 在左侧粘贴 JSON 后自动解析");
            return;
        }

        SetStatus(Neutral(), "解析中…");
        // 基线 = 上一次的树（点示例/清空后的首次解析不对比）
        var baseline = _freshContent ? null : JsonNodeVM.BuildBaseline(_root);
        _freshContent = false;
        bool hide = _hideEmpty;

        try
        {
            var (doc, root, pretty, nodeCount, ms) = await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var d = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 1024,
                });
                int count = 0;
                var node = JsonNodeVM.Root(d.RootElement, hide, ref count);
                node.MarkDiff(baseline);
                sw.Stop();
                string p = JsonSerializer.Serialize(d.RootElement, IndentOptions);
                return (d, node, p, count, sw.ElapsedMilliseconds);
            });

            if (seq != _seq) { doc.Dispose(); return; }

            _doc?.Dispose();
            _doc = doc;
            _pretty = pretty;
            _root = root;
            _parsedText = text;
            _prevBaseline = baseline; // 隐藏空值重建时复用同一基线

            _root.ApplyDefaultExpand(nodeCount > 6000 ? 1 : nodeCount > 1500 ? 3 : 12);

            ResultTree.ItemsSource = new[] { _root };
            EmptyHint.IsVisible = false;
            SetStatus(Ok(), $"解析成功 · {nodeCount:N0} 个节点 · {ms} ms");
            ShowSize(text);
        }
        catch (JsonException ex)
        {
            if (seq != _seq) return;
            ResultTree.ItemsSource = null;
            EmptyHint.IsVisible = true;
            _doc = null; _pretty = null; _root = null;
            long line = (ex.LineNumber ?? 0) + 1;
            SetStatus(Err(), $"JSON 语法错误（第 {line} 行）：{ex.Message}");
            ShowSize(text);
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;
            SetStatus(Err(), $"解析失败：{ex.Message}");
        }
    }

    private void ShowSize(string text)
    {
        long bytes = Encoding.UTF8.GetByteCount(text);
        SizeText.Text = $"{text.Length:N0} 字符 · {FormatSize(bytes)}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };

    private void SetStatus(IBrush dot, string message)
    {
        StatusDot.Fill = dot;
        StatusText.Text = message;
    }

    private static IBrush Ok() => GetBrush("StatusOk");
    private static IBrush Err() => GetBrush("StatusErr");
    private static IBrush Neutral() => GetBrush("InkHint");
    private static IBrush GetBrush(string key) =>
        App.Current.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray;

    // ---------- 工具栏 ----------

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (_pretty is not null)
        {
            InputBox.Text = _pretty;
            SetStatus(Ok(), "已格式化源码");
        }
        else
        {
            _debounce.Stop();
            RunParse();
        }
    }

    private void OnMinifyClick(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        InputBox.Text = JsonSerializer.Serialize(_doc.RootElement, CompactEscapeOptions);
        SetStatus(Ok(), "已压缩（中文转 Unicode）");
    }

    private void OnMinifyRawClick(object? sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        InputBox.Text = JsonSerializer.Serialize(_doc.RootElement, CompactRawOptions);
        SetStatus(Ok(), "已压缩（保留中文原文）");
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        string text = _pretty ?? InputBox.Text ?? "";
        if (text.Length == 0) return;
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(text);
            SetStatus(Ok(), "已复制到剪贴板");
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        InputBox.Focus();
        _freshContent = true;
    }

    private void OnExpandAllClick(object? sender, RoutedEventArgs e) => _root?.SetAllExpanded(true);

    private void OnCollapseAllClick(object? sender, RoutedEventArgs e)
    {
        if (_root is null) return;
        _root.SetAllExpanded(false);
        _root.IsExpanded = true;
    }

    private void OnSampleClick(object? sender, RoutedEventArgs e)
    {
        InputBox.Text = SampleJson;
        InputBox.CaretIndex = 0;
        _freshContent = true;
    }

    // ---------- 隐藏空值 ----------

    private void OnHideEmptyToggle(object? sender, RoutedEventArgs e)
    {
        _hideEmpty = HideEmptyToggle.IsChecked == true;
        if (_doc is null) return;
        int count = 0;
        var node = JsonNodeVM.Root(_doc.RootElement, _hideEmpty, ref count);
        node.MarkDiff(_prevBaseline); // 内容未变，复用同一基线重现标红
        node.ApplyDefaultExpand(count > 6000 ? 1 : count > 1500 ? 3 : 12);
        _root = node;
        _selected = null;
        ResultTree.ItemsSource = new[] { node };
        SetStatus(Ok(), _hideEmpty ? "已隐藏空值字段" : "已显示全部字段");
    }

    // ---------- 单击源码定位 ----------

    private void OnInputPointerReleased(object? sender, PointerEventArgs e)
    {
        Dispatcher.UIThread.Post(() => LocateFromCaret(InputBox.CaretIndex));
    }

    private void LocateFromCaret(int caret)
    {
        string text = InputBox.Text ?? "";
        if (_root is null || _parsedText != text)
        {
            _debounce.Stop();
            RunParse();
            return;
        }

        var segs = JsonPathLocator.FindPathAt(text, caret);
        if (segs is null) return;

        var chain = new List<JsonNodeVM> { _root };
        var node = _root;
        foreach (var seg in segs)
        {
            node = node.Children.FirstOrDefault(c => c.Key == seg);
            if (node is null) return;
            chain.Add(node);
        }

        for (int i = 0; i < chain.Count - 1; i++)
            chain[i].IsExpanded = true;
        if (node.HasChildren) node.IsExpanded = true;

        ResultTree.UpdateLayout();
        var tvi = FindContainer(chain);
        if (tvi is not null)
        {
            tvi.IsSelected = true;
            tvi.BringIntoView();
            SetStatus(Ok(), "已定位：" + Describe(segs));
        }
        else
        {
            SetStatus(Ok(), "已展开到：" + Describe(segs));
        }
    }

    private TreeViewItem? FindContainer(List<JsonNodeVM> chain)
    {
        ItemsControl parent = ResultTree;
        TreeViewItem? tvi = null;
        for (int i = 0; i < chain.Count; i++)
        {
            var container = ContainerOf(parent, chain[i]);
            if (container is null) return i == 0 ? null : tvi;
            tvi = container;
            parent = container;
        }
        return tvi;
    }

    private static TreeViewItem? ContainerOf(ItemsControl parent, JsonNodeVM item)
    {
        if (parent.Items is not System.Collections.IList list) return null;
        int index = list.IndexOf(item);
        return index >= 0 ? parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem : null;
    }

    private static string Describe(List<string> segs)
        => segs.Count == 0 ? "JSON" : string.Join(" › ", segs);

    // ---------- 树选中 → 子树高亮 ----------

    private JsonNodeVM? _selected;

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var added = e.AddedItems.OfType<JsonNodeVM>().FirstOrDefault();
        var removed = e.RemovedItems.OfType<JsonNodeVM>().FirstOrDefault();
        if (ReferenceEquals(added, _selected)) return;
        removed?.ClearHighlight();
        _selected = added;
        added?.ApplyHighlight();
    }

    // ---------- 右键复制 ----------

    private JsonNodeVM? _menuNode;

    private void BuildTreeMenu()
    {
        var menu = new ContextMenu();
        var miValue = new MenuItem { Header = "复制值" };
        miValue.Click += (_, _) =>
        {
            if (_menuNode is not { } n) return;
            CopyText(n.ScalarRaw ?? n.ToJsonText());
        };
        var miJson = new MenuItem { Header = "复制该节点 JSON" };
        miJson.Click += (_, _) =>
        {
            if (_menuNode is not { } n) return;
            CopyText(n.ToJsonText());
        };
        menu.Items.Add(miValue);
        menu.Items.Add(miJson);
        ResultTree.ContextMenu = menu;

        // 右键按下时命中测试出目标行
        ResultTree.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
            Visual? v = e.Source as Visual;
            while (v is not null && v is not TreeViewItem)
                v = v.GetVisualParent();
            _menuNode = (v as TreeViewItem)?.DataContext as JsonNodeVM;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private async void CopyText(string text)
    {
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(text);
            SetStatus(Ok(), "已复制");
        }
    }

    // ---------- 设置 ----------

    private async void OnGearClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SettingsWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.ThemeChanged += OnThemeChanged;
            win.FontSizeChanged += ApplyFontSize;
            await win.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetStatus(Err(), "打开设置失败：" + ex.Message);
        }
    }

    private void OnThemeChanged(string id)
    {
        ThemeManager.Apply(id, applyResources: true);
        _root?.ApplyPaletteRecursive();
        Settings.Data.Theme = id;
        Settings.Save();
    }

    public void ApplyFontSize(double size)
    {
        InputBox.FontSize = size;
        ResultTree.FontSize = size;
        Settings.Data.FontSize = size;
        Settings.Save();
    }
}
