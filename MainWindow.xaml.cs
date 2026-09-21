using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
    private readonly DispatcherTimer _toastTimer;
    private int _seq;
    private JsonDocument? _doc;
    private string? _pretty;
    private JsonNodeVM? _root;
    private string? _parsedText;
    /// <summary>示例/清空后的首次解析：重置修改对比基线，不标红。</summary>
    private bool _freshContent;
    /// <summary>首次格式化（或点示例/清空后）固化的原始基线：后续修改都与它比，改回原值即恢复原色。</summary>
    private JsonNodeVM.BaselineInfo? _originBaseline;
    private bool _hideEmpty;

    private static Brush StatusOk => (Brush)Application.Current.TryFindResource("StatusOk");
    private static Brush StatusError => (Brush)Application.Current.TryFindResource("StatusErr");
    private static Brush StatusNeutral => (Brush)Application.Current.TryFindResource("InkHint");

    // 中文等非 ASCII 字符保持原样，不转义成 \uXXXX
    private static readonly JsonSerializerOptions IndentOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    // 默认编码器：中文等非 ASCII 字符转义成 \uXXXX
    private static readonly JsonSerializerOptions CompactEscapeOptions = new();

    public MainWindow()
    {
        InitializeComponent();
        Placeholder.Visibility = Visibility.Visible;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunParse(); };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => _doc?.Dispose();
        ResultTree.ContextMenu = BuildTreeMenu();

        ApplyFontSize(Settings.Data.FontSize, save: false);
        if (Settings.Data.DefaultFullscreen)
            WindowState = WindowState.Maximized;

        InputBox.Focus();
    }

    private static Brush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // ---------- 解析 ----------

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void RunParse()
    {
        int seq = ++_seq;
        string text = InputBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            _doc?.Dispose();
            _doc = null;
            _pretty = null;
            _root = null;
            _originBaseline = null;
            ResultTree.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Visible;
            SizeText.Text = "";
            SetStatus(StatusNeutral, "就绪 — 在左侧粘贴 JSON 后自动解析");
            return;
        }

        SetStatus(StatusNeutral, "解析中…");
        var baseline = _freshContent ? null : _originBaseline;
        bool fresh = _freshContent;
        bool hide = _hideEmpty;
        var snapshot = fresh ? null : CollectExpanded(_root); // 保留用户展开状态
        _freshContent = false;
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 1024,
            };

            (JsonDocument doc, JsonNodeVM root, string pretty, int nodeCount, long ms, List<JsonNodeVM>? chain) =
                await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var d = JsonDocument.Parse(text, options);
                    int count = 0;
                    var node = JsonNodeVM.Root(d.RootElement, hide, ref count);
                    node.MarkDiff(baseline);
                    if (fresh)
                        node.ApplyDefaultExpand(count > 6000 ? 1 : count > 1500 ? 3 : 12);
                    else
                        RestoreExpanded(node, "", snapshot!); // 同一文档被修改：保持展开不折叠
                    var chain = fresh ? null : FindFirstModifiedChain(node);
                    sw.Stop();
                    string p = JsonSerializer.Serialize(d.RootElement, IndentOptions);
                    return (d, node, p, count, sw.ElapsedMilliseconds, chain);
                });

            if (seq != _seq)
            {
                doc.Dispose();
                return;
            }

            _doc?.Dispose();
            _doc = doc;
            _pretty = pretty;
            _root = root;
            _parsedText = text;
            if (fresh)
                _originBaseline = JsonNodeVM.BuildBaseline(root); // 固化原始基线

            if (chain is not null)
            {
                // 定位到本次修改处：展开祖先并滚到可视区
                for (int i = 0; i < chain.Count - 1; i++)
                    chain[i].IsExpanded = true;
                ResultTree.ItemsSource = new[] { root };
                ResultTree.UpdateLayout();
                FindContainer(chain)?.BringIntoView();
            }
            else
            {
                ResultTree.ItemsSource = new[] { root };
            }
            EmptyHint.Visibility = Visibility.Collapsed;
            SetStatus(StatusOk, $"✔ 解析成功 · {nodeCount:N0} 个节点 · {ms} ms");
            ShowSize(text);
        }
        catch (JsonException ex)
        {
            if (seq != _seq) return;
            ResultTree.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Visible;
            _doc = null; _pretty = null; _root = null;
            _parsedText = text;
            long line = (ex.LineNumber ?? 0) + 1;
            SetStatus(StatusError, $"✖ JSON 语法错误（第 {line} 行）：{ex.Message}");
            ShowSize(text);
        }
        catch (Exception ex)
        {
            if (seq != _seq) return;
            ResultTree.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Visible;
            SetStatus(StatusError, $"✖ 解析失败：{ex.Message}");
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

    private void SetStatus(Brush brush, string message)
    {
        StatusText.Foreground = brush;
        StatusText.Text = message;
    }

    // ---------- 工具栏 ----------

    private void OnFormatClick(object sender, RoutedEventArgs e)
    {
        if (_doc is not null && _pretty is not null)
        {
            InputBox.Text = _pretty;
            SetStatus(StatusOk, "已格式化源码");
        }
        else
        {
            _debounce.Stop();
            RunParse(); // 输入非法时刷新错误提示
        }
    }

    // 压缩（默认）：中文转 Unicode
    private void OnMinifyClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        InputBox.Text = JsonSerializer.Serialize(_doc.RootElement, CompactEscapeOptions);
        SetStatus(StatusOk, "已压缩（中文转 Unicode）");
    }

    // 压缩原文：保留中文等字符原样
    private void OnMinifyRawClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        InputBox.Text = JsonSerializer.Serialize(_doc.RootElement, CompactOptions);
        SetStatus(StatusOk, "已压缩（保留中文原文）");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = _pretty ?? InputBox.Text;
        if (text.Length == 0) return;
        if (CopyToClipboard(text))
        {
            SetStatus(StatusOk, "已复制到剪贴板");
            ShowToast("\u2714 复制成功");
        }
    }

    /// <summary>顶部居中轻提示，0.5 秒后自动消失（连续触发会重置计时）。</summary>
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        InputBox.Focus();
        _freshContent = true; // 清空后重新粘贴属于新内容，不与旧内容对比标红
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs e) => _root?.SetAllExpanded(true);

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        if (_root is null) return;
        _root.SetAllExpanded(false);
        _root.IsExpanded = true; // 保留根节点展开
    }

    private void OnSampleClick(object sender, RoutedEventArgs e)
    {
        InputBox.Text = SampleJson;
        InputBox.SelectionStart = 0;
        _freshContent = true; // 载入示例是新内容，不与旧内容对比标红
    }

    private static bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 节点选中高亮 ----------

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.OldValue is JsonNodeVM old) old.ClearHighlight();
        (e.NewValue as JsonNodeVM)?.ApplyHighlight();
    }

    // ---------- 左右联动：单击源码定位节点 ----------

    private void OnInputLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 光标点在哪，右侧就定位到对应节点
        int caret = InputBox.GetCharacterIndexFromPoint(e.GetPosition(InputBox), snapToText: true);
        if (caret < 0) caret = InputBox.CaretIndex;
        LocateFromCaret(caret);
    }

    private void LocateFromCaret(int caret)
    {
        string text = InputBox.Text;
        if (_root is null || _parsedText != text)
        {
            // 树还是旧内容（或刚出错），先刷新；本次点击不定位
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
            if (node is null) return; // 文本与树不一致（容错）
            chain.Add(node);
        }

        for (int i = 0; i < chain.Count - 1; i++)
            chain[i].IsExpanded = true; // 逐级展开祖先
        if (node.HasChildren) node.IsExpanded = true;

        var tvi = FindContainer(chain);

        if (tvi is not null)
        {
            tvi.IsSelected = true;   // 触发 SelectedItemChanged → 子树高亮
            tvi.BringIntoView();
            SetStatus(StatusOk, "已定位：" + Describe(segs));
        }
        else
        {
            SetStatus(StatusOk, "已展开到：" + Describe(segs));
        }
    }

    /// <summary>沿链逐级下钻取节点容器：嵌套容器挂在各父节点生成器上，虚拟化下需多次布局。</summary>
    private TreeViewItem? FindContainer(List<JsonNodeVM> chain)
    {
        TreeViewItem? tvi = ResultTree.ItemContainerGenerator.ContainerFromItem(chain[0]) as TreeViewItem;
        for (int i = 1; tvi is not null && i < chain.Count; i++)
        {
            TreeViewItem? child = null;
            for (int attempt = 0; attempt < 10 && child is null; attempt++)
            {
                ResultTree.UpdateLayout();
                child = tvi.ItemContainerGenerator.ContainerFromItem(chain[i]) as TreeViewItem;
            }
            tvi = child;
        }
        return tvi;
    }

    /// <summary>收集当前树的展开路径快照（重新解析后恢复，避免折叠跳动）。</summary>
    private static HashSet<string> CollectExpanded(JsonNodeVM? root)
    {
        var set = new HashSet<string>();
        if (root is not null)
            Collect(root, "", set);
        return set;

        static void Collect(JsonNodeVM n, string path, HashSet<string> s)
        {
            if (n.IsExpanded) s.Add(path);
            foreach (var c in n.Children)
                Collect(c, path.Length == 0 ? c.Key : path + '\u0001' + c.Key, s);
        }
    }

    private static void RestoreExpanded(JsonNodeVM n, string path, HashSet<string> s)
    {
        n.IsExpanded = n.IsRoot || s.Contains(path);
        foreach (var c in n.Children)
            RestoreExpanded(c, path.Length == 0 ? c.Key : path + '\u0001' + c.Key, s);
    }

    /// <summary>DFS 找首个被修改节点的根→节点链（用于自动定位）。</summary>
    private static List<JsonNodeVM>? FindFirstModifiedChain(JsonNodeVM root)
    {
        var chain = new List<JsonNodeVM>();
        return Dfs(root) ? chain : null;

        bool Dfs(JsonNodeVM n)
        {
            chain.Add(n);
            if (!n.IsRoot && (n.IsKeyModified || n.IsValueModified)) return true;
            foreach (var c in n.Children)
                if (Dfs(c)) return true;
            chain.RemoveAt(chain.Count - 1);
            return false;
        }
    }

    private static string Describe(List<string> segs)
        => segs.Count == 0 ? "JSON" : string.Join(" › ", segs);

    // ---------- 右键菜单 ----------

    private JsonNodeVM? _menuNode;

    private ContextMenu BuildTreeMenu()
    {
        var miValue = new MenuItem { Header = "复制值" };
        miValue.Click += OnMenuCopyValue;
        var miJson = new MenuItem { Header = "复制该节点 JSON" };
        miJson.Click += OnMenuCopyNodeJson;
        return new ContextMenu { Items = { miValue, miJson } };
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menuNode = HitNode();
        if (_menuNode is null)
            e.Handled = true; // 空白处不弹菜单
    }

    private JsonNodeVM? HitNode()
    {
        DependencyObject? hit = ResultTree.InputHitTest(Mouse.GetPosition(ResultTree)) as DependencyObject;
        while (hit is not null && hit is not TreeViewItem)
            hit = VisualTreeHelper.GetParent(hit);
        return (hit as TreeViewItem)?.DataContext as JsonNodeVM;
    }

    private void OnMenuCopyValue(object sender, RoutedEventArgs e)
    {
        if (_menuNode is not { } node) return;
        string text = node.ScalarRaw ?? node.ToJsonText();
        if (CopyToClipboard(text)) SetStatus(StatusOk, "已复制值");
    }

    private void OnMenuCopyNodeJson(object sender, RoutedEventArgs e)
    {
        if (_menuNode is not { } node) return;
        if (CopyToClipboard(node.ToJsonText())) SetStatus(StatusOk, "已复制该节点 JSON");
    }

    // ---------- 隐藏空值 ----------

    private void OnHideEmptyToggle(object sender, RoutedEventArgs e)
    {
        _hideEmpty = HideEmptyToggle.IsChecked == true;
        if (_doc is null) return;
        int count = 0;
        var node = JsonNodeVM.Root(_doc.RootElement, _hideEmpty, ref count);
        var snapshot = CollectExpanded(_root);
        node.MarkDiff(_originBaseline); // 与原始基线比，改回原值不标红
        RestoreExpanded(node, "", snapshot);
        var chain = FindFirstModifiedChain(node);
        _root = node;
        ResultTree.ItemsSource = new[] { node };
        if (chain is not null)
        {
            for (int i = 0; i < chain.Count - 1; i++)
                chain[i].IsExpanded = true;
            ResultTree.UpdateLayout();
            FindContainer(chain)?.BringIntoView();
        }
        SetStatus(StatusOk, _hideEmpty ? "已隐藏 null 值和空数组" : "已显示全部字段");
    }

    // ---------- 设置 ----------

    private void OnGearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SettingsWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            win.ThemeChanged += OnThemeChanged;
            win.FontSizeChanged += fs => ApplyFontSize(fs);
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus(StatusError, "打开设置失败：" + ex.Message);
        }
    }

    private void OnThemeChanged(string id)
    {
        ThemeManager.Apply(id);
        _root?.ApplyPaletteRecursive();
        Settings.Data.Theme = id;
        Settings.Save();
    }

    public void ApplyFontSize(double size, bool save = true)
    {
        InputBox.FontSize = size;
        ResultTree.FontSize = size;
        if (save)
        {
            Settings.Data.FontSize = size;
            Settings.Save();
        }
    }
}
