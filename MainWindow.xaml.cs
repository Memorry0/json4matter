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

        // 多媒体卡片：值点击打开、点卡片外/Esc 关闭
        Media.Controls.MediaValueControl.MediaClicked += OnMediaClicked;
        PreviewMouseLeftButtonDown += OnGlobalPreviewMouseDown;
        PreviewKeyDown += OnWindowPreviewKeyDown;

        ApplyFontSize(Settings.Data.FontSize, save: false);
        if (Settings.Data.DefaultFullscreen)
            WindowState = WindowState.Maximized;

        // 上次会话已开启多媒体：启动即生效，并后台预热解码组件
        Media.MediaSupport.Enabled = Settings.Data.EnableMedia;
        if (Settings.Data.EnableMedia)
        {
            _ = Task.Run(async () =>
            {
                await Media.PluginManager.EnsureReadyAsync();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Media.PluginManager.Ready)
                        SetStatus(StatusOk, "多媒体组件就绪");
                });
            });
        }

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
                    {
                        int restoreIndex = 0;
                        RestoreExpanded(node, ref restoreIndex, snapshot!); // 同一文档被修改：保持展开不折叠
                    }
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
                for (int i = 0; i < chain.Count; i++)
                    chain[i].IsExpanded = true;
                ResultTree.ItemsSource = new[] { root };
                ResultTree.UpdateLayout();
                FindContainerOrRealize(chain)?.BringIntoView();
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
        // 等本轮鼠标 DOWN/UP 输入管线走完再定位：定位含展开+下钻+滚动的重活，
        // 在 DOWN 处理中同步执行会让 UP 到达时命中元素已变化，WPF 捕获不释放、输入被吞。
        // Background 优先级确保排在 UP 之后（Input 优先级仍可能抢在 UP 前执行）。
        Dispatcher.BeginInvoke(() => LocateFromCaret(caret),
            System.Windows.Threading.DispatcherPriority.Background);
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

        var tvi = FindContainerOrRealize(chain);

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

    /// <summary>
    /// 兼空壳：ResultTree 已改为静态关闭虚拟化（见 XAML 注释），展开节点的容器总是已生成，
    /// 保留此名以少动调用点。
    /// </summary>
    /// <summary>
    /// 虚拟化下目标容器可能未生成（宽层数组/大文件）。按目标的前序扁平行索引滚动，
    /// 让虚拟化面板在目标附近生成容器后再下钻取容器。
    /// </summary>
    private TreeViewItem? FindContainerOrRealize(List<JsonNodeVM> chain)
    {
        var tvi = FindContainer(chain);
        if (tvi is not null) return tvi;
        if (chain.Count < 2 || _root is null) return null;

        int idx = FlatIndexOf(_root, chain[^1]);
        var sv = FindScrollViewer(ResultTree);
        if (idx < 0 || sv is null) return null;

        // 分层虚拟化面板的 offset 是像素，且 extent 是估算值：迭代滚动逼近目标行
        for (int round = 0; round < 3; round++)
        {
            int total = FlatCount(_root);
            if (total <= 0 || sv.ExtentHeight <= 0) break;
            double rowH = sv.ExtentHeight / total;
            sv.ScrollToVerticalOffset(Math.Max(0, (idx - 3) * rowH));
            ResultTree.UpdateLayout();
            var t = FindContainer(chain);
            if (t is not null) return t;
        }
        return null;
    }

    /// <summary>当前展开状态下的可见总行数（前序）。</summary>
    private static int FlatCount(JsonNodeVM? root)
    {
        if (root is null) return 0;
        int c = 0;
        Walk(root);
        return c;

        void Walk(JsonNodeVM n)
        {
            c++;
            if (n.IsExpanded)
                foreach (var ch in n.Children)
                    Walk(ch);
        }
    }

    /// <summary>目标节点在当前展开状态下的前序扁平行索引（0 起）。</summary>
    private static int FlatIndexOf(JsonNodeVM root, JsonNodeVM target)
    {
        int idx = 0;
        return Walk(root);

        int Walk(JsonNodeVM n)
        {
            if (ReferenceEquals(n, target)) return idx;
            idx++;
            if (n.IsExpanded)
                foreach (var c in n.Children)
                {
                    var r = Walk(c);
                    if (r >= 0) return r;
                }
            return -1;
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject? v)
    {
        if (v is null) return null;
        if (v is System.Windows.Controls.ScrollViewer sv) return sv;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v);
        for (int i = 0; i < n; i++)
        {
            var r = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(v, i));
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>
    /// 收集当前树的展开状态快照（前序序号）。按位置而非路径记录：
    /// 改 key 会改变子树路径，但不改变前序位置，展开状态因此得以保留。
    /// </summary>
    private static HashSet<int> CollectExpanded(JsonNodeVM? root)
    {
        var set = new HashSet<int>();
        if (root is not null)
        {
            int index = 0;
            Collect(root, ref index, set);
        }
        return set;

        static void Collect(JsonNodeVM n, ref int index, HashSet<int> s)
        {
            int self = index++;
            if (n.IsExpanded) s.Add(self);
            foreach (var c in n.Children)
                Collect(c, ref index, s);
        }
    }

    private static void RestoreExpanded(JsonNodeVM n, ref int index, HashSet<int> s)
    {
        int self = index++;
        n.IsExpanded = n.IsRoot || s.Contains(self);
        foreach (var c in n.Children)
            RestoreExpanded(c, ref index, s);
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

    // ---------- 搜索（键 / 值） ----------

    private bool _lastSearchByKey = true;

    private void OnSearchKey(object sender, RoutedEventArgs e) => RunSearch(byKey: true);
    private void OnSearchValue(object sender, RoutedEventArgs e) => RunSearch(byKey: false);

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunSearch(_lastSearchByKey);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0)
            ClearSearchHits(); // 清空输入即撤销高亮
    }

    private void RunSearch(bool byKey)
    {
        _lastSearchByKey = byKey;
        string q = SearchBox.Text.Trim();
        if (_root is null)
        {
            SetStatus(StatusNeutral, "无内容可搜索");
            return;
        }
        if (q.Length == 0)
        {
            ClearSearchHits();
            SetStatus(StatusNeutral, "已清除搜索高亮");
            return;
        }

        var chain = new List<JsonNodeVM>();
        List<JsonNodeVM>? firstHit = null;
        int hits = Walk(_root, chain, ref firstHit);

        if (hits > 0 && firstHit is not null)
            FindContainerOrRealize(firstHit)?.BringIntoView();

        SetStatus(hits > 0 ? StatusOk : StatusError,
            $"{(byKey ? "键" : "值")}搜索「{q}」：{(hits > 0 ? $"{hits} 个匹配" : "无匹配")}");
        return;

        int Walk(JsonNodeVM node, List<JsonNodeVM> path, ref List<JsonNodeVM>? first)
        {
            path.Add(node);
            int count = 0;
            bool hit = byKey
                ? node.Key is not null && node.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                : node.IsScalar && (node.SlotText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            if (byKey) node.SearchHitKey = hit; else node.SearchHitValue = hit;
            if (hit)
            {
                count++;
                // 展开祖先使命中可见
                for (int i = 0; i < path.Count - 1; i++)
                    path[i].IsExpanded = true;
                first ??= path.ToList();
            }
            foreach (var c in node.Children)
                count += Walk(c, path, ref first);
            path.RemoveAt(path.Count - 1);
            return count;
        }
    }

    private void ClearSearchHits()
    {
        if (_root is null) return;
        Clear(_root);
        return;

        static void Clear(JsonNodeVM n)
        {
            n.SearchHitKey = false;
            n.SearchHitValue = false;
            foreach (var c in n.Children)
                Clear(c);
        }
    }

    // ---------- 多媒体固定卡片 ----------

    private void OnMediaClicked(object? sender, JsonNodeVM node)
    {
        OpenMediaCard(node);
    }

    private void OpenMediaCard(JsonNodeVM node)
    {
        var card = new Media.Controls.MediaCard();
        card.Closed += (_, _) => CloseMediaCard();
        MediaCardHost.Content = card;
        MediaCardHost.Visibility = Visibility.Visible;
        card.Bind(node);
    }

    private void CloseMediaCard()
    {
        if (MediaCardHost.Content is Media.Controls.MediaCard card)
        {
            MediaCardHost.Content = null;
        }
        MediaCardHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>Esc 关闭多媒体固定卡片。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && MediaCardHost.Content is Media.Controls.MediaCard)
        {
            CloseMediaCard();
            e.Handled = true;
        }
    }

    /// <summary>点在卡片外任意处 → 关闭固定卡片。</summary>
    private void OnGlobalPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MediaCardHost.Content is not Media.Controls.MediaCard) return;
        var v = e.OriginalSource as DependencyObject;
        while (v is not null)
        {
            if (ReferenceEquals(v, MediaCardHost.Content)) return; // 点在卡片内
            v = System.Windows.Media.VisualTreeHelper.GetParent(v);
        }
        CloseMediaCard();
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
        { int ri = 0; RestoreExpanded(node, ref ri, snapshot); }
        var chain = FindFirstModifiedChain(node);
        _root = node;
        ResultTree.ItemsSource = new[] { node };
        if (chain is not null)
        {
            for (int i = 0; i < chain.Count; i++)
                chain[i].IsExpanded = true;
            ResultTree.UpdateLayout();
            FindContainerOrRealize(chain)?.BringIntoView();
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

    /// <summary>多媒体开关：开启时异步拉取组件并重建树生效。</summary>
    private void OnMediaToggled(bool on)
    {
        Media.MediaSupport.Enabled = on;
        if (on)
        {
            SetStatus(StatusOk, "多媒体支持已开启，正在准备组件…");
            _ = Task.Run(async () =>
            {
                await Media.PluginManager.EnsureReadyAsync();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Media.PluginManager.Ready)
                        SetStatus(StatusOk, "多媒体组件就绪");
                    else
                        SetStatus(StatusError, "多媒体组件下载失败：" + Media.PluginManager.Error + "（图片/PDF/视频基本信息仍可用）");
                    RebuildTreeForMedia();
                });
            });
        }
        else
        {
            SetStatus(StatusOk, "多媒体支持已关闭");
        }
        RebuildTreeForMedia();
    }

    /// <summary>开关变化后按当前 _doc 重建树（重新做媒体识别）。</summary>
    private void RebuildTreeForMedia()
    {
        if (_doc is null) return;
        int count = 0;
        var node = JsonNodeVM.Root(_doc.RootElement, _hideEmpty, ref count);
        node.MarkDiff(_originBaseline);
        var snapshot = CollectExpanded(_root);
        { int ri = 0; RestoreExpanded(node, ref ri, snapshot); }
        var chain = FindFirstModifiedChain(node);
        _root = node;
        ResultTree.ItemsSource = new[] { node };
        if (chain is not null)
        {
            for (int i = 0; i < chain.Count; i++)
                chain[i].IsExpanded = true;
            ResultTree.UpdateLayout();
            FindContainerOrRealize(chain)?.BringIntoView();
        }
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
