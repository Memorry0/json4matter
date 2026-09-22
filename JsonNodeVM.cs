using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace JsonFormatter;

/// <summary>JSON 树的节点视图模型。对象节点默认展开，数组节点默认折叠并在标题上标注元素数量。</summary>
public sealed class JsonNodeVM : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelectedNode;
    private bool _isSubtreeHighlighted;

    public string Key { get; }
    public List<JsonNodeVM> Children { get; } = new();

    public bool IsArray { get; private set; }
    public bool IsObject { get; private set; }
    public bool IsRoot { get; private set; }
    /// <summary>标量节点的原始 JSON 文本（字符串带引号），用于复制。</summary>
    public string? ScalarRaw { get; private set; }
    /// <summary>原始 JSON 类型（主题切换后按类型重着色）。</summary>
    public JsonValueKind Kind { get; private set; }
    public bool IsKeyModified { get; private set; }
    public bool IsValueModified { get; private set; }
    /// <summary>该子树是否含非空内容（隐藏空值时用于过滤）。</summary>
    public bool HasContent { get; private set; }
    /// <summary>值为可预览的媒体 URL（多媒体开关开启时）。</summary>
    public bool IsMediaUrl => MediaKind != Media.MediaKind.None;
    public Media.MediaKind MediaKind { get; private set; } = Media.MediaKind.None;
    /// <summary>未截断的原始 URL。</summary>
    public string? UrlValue { get; private set; }
    /// <summary>用于修改对比的规范值：字符串取解码后的内容（与 \uXXXX 转义形式无关），其余用原始文本。</summary>
    public string ScalarCompare { get; private set; } = "";
    public bool IsScalar => ScalarRaw is not null;
    public bool HasChildren => Children.Count > 0;

    public string KeyText { get; }
    private Brush _keyBrush;
    /// <summary>key 颜色：被改名/新增时变红。</summary>
    public Brush KeyBrush
    {
        get => _keyBrush;
        private set { if (_keyBrush != value) { _keyBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyBrush))); } }
    }
    private FontWeight _keyWeight;
    public FontWeight KeyWeight
    {
        get => _keyWeight;
        private set { if (_keyWeight != value) { _keyWeight = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyWeight))); } }
    }

    /// <summary>" : " 分隔符，仅在右侧有内容时显示。</summary>
    public Visibility SepVisibility { get; private set; } = Visibility.Collapsed;

    public string? SlotText { get; private set; }
    public Brush SlotBrush { get; private set; } = Brushes.Transparent;
    public FontWeight SlotWeight { get; private set; } = FontWeights.Normal;
    public FontStyle SlotStyle { get; private set; } = FontStyles.Normal;
    public string? SlotTooltip { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
    }

    /// <summary>当前被点中的节点（高亮加重）。</summary>
    public bool IsSelectedNode
    {
        get => _isSelectedNode;
        set { if (_isSelectedNode != value) { _isSelectedNode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedNode))); } }
    }

    /// <summary>处于选中节点的子树范围内（整块浅色高亮）。</summary>
    public bool IsSubtreeHighlighted
    {
        get => _isSubtreeHighlighted;
        set { if (_isSubtreeHighlighted != value) { _isSubtreeHighlighted = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSubtreeHighlighted))); } }
    }

    /// <summary>把该节点标记为选中，并将其整个子树标记为高亮。</summary>
    public void ApplyHighlight()
    {
        IsSelectedNode = true;
        MarkSubtree(this);

        static void MarkSubtree(JsonNodeVM n)
        {
            n.IsSubtreeHighlighted = true;
            foreach (var c in n.Children)
                MarkSubtree(c);
        }
    }

    /// <summary>清除该节点及子树上的高亮标记（已干净则剪枝）。</summary>
    public void ClearHighlight()
    {
        if (!_isSelectedNode && !_isSubtreeHighlighted) return;
        IsSelectedNode = false;
        IsSubtreeHighlighted = false;
        foreach (var c in Children)
            c.ClearHighlight();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private JsonNodeVM(string key, bool isRoot)
    {
        Key = key;
        IsRoot = isRoot;
        KeyText = key;
        _keyWeight = isRoot ? FontWeights.SemiBold : FontWeights.Normal;
        var p = ThemeManager.Current;
        KeyBrush = isRoot ? p.RootKey : isIndexKey(key) ? p.IndexKey : p.Key;

        static bool isIndexKey(string k) => k.StartsWith('[');
    }

    // ---------- 工厂 ----------

    public static JsonNodeVM Root(JsonElement root, bool hideEmpty, ref int nodeCount)
    {
        var vm = new JsonNodeVM("JSON", isRoot: true);
        Fill(vm, root, hideEmpty, ref nodeCount);
        return vm;
    }

    private static JsonNodeVM Child(string key, JsonElement value, bool hideEmpty, ref int nodeCount)
    {
        var vm = new JsonNodeVM(key, isRoot: false);
        Fill(vm, value, hideEmpty, ref nodeCount);
        return vm;
    }

    private static void Fill(JsonNodeVM vm, JsonElement e, bool hideEmpty, ref int nodeCount)
    {
        nodeCount++;
        vm.Kind = e.ValueKind;
        var p = ThemeManager.Current;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                vm.IsObject = true;
                foreach (var prop in e.EnumerateObject())
                {
                    var child = Child(prop.Name, prop.Value, hideEmpty, ref nodeCount);
                    if (!hideEmpty || child.HasContent)
                        vm.Children.Add(child);
                }
                if (vm.Children.Count == 0 && !hideEmpty)
                {
                    vm.SepVisibility = Visibility.Visible;
                    vm.SlotText = "{}";
                    vm.SlotBrush = p.Empty;
                }
                break;

            case JsonValueKind.Array:
                vm.IsArray = true;
                int i = 0, total = 0;
                foreach (var item in e.EnumerateArray())
                {
                    var child = Child($"[{i++}]", item, hideEmpty, ref nodeCount);
                    total++;
                    if (!hideEmpty || child.HasContent)
                        vm.Children.Add(child);
                }
                vm.SepVisibility = Visibility.Visible;
                vm.SlotText = $"[{total}]";
                vm.SlotBrush = p.Count;
                vm.SlotWeight = FontWeights.SemiBold;
                break;

            default:
                SetScalar(vm, e);
                break;
        }

        vm.HasContent = vm.IsScalar
            ? vm.ScalarRaw is not ("null" or "\"\"")
            : vm.Children.Count > 0;
    }

    private static void SetScalar(JsonNodeVM vm, JsonElement e)
    {
        string raw = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            _ => e.GetRawText(),
        };
        vm.ScalarRaw = e.GetRawText();
        vm.ScalarCompare = e.ValueKind == JsonValueKind.String ? "s:" + raw : vm.ScalarRaw;

        vm.SepVisibility = Visibility.Visible;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                // 媒体 URL 识别（截断展示不影响原始 URL）
                if (Media.MediaSupport.Enabled)
                {
                    vm.MediaKind = Media.UrlMedia.Detect(raw);
                    if (vm.MediaKind != Media.MediaKind.None)
                        vm.UrlValue = raw;
                }
                string escaped = Escape(raw);
                if (escaped.Length > 268)
                {
                    vm.SlotText = escaped[..264] + "…";
                    string tip = escaped.Length > 5002 ? escaped[..5000] + "…" : escaped;
                    vm.SlotTooltip = tip;
                }
                else
                {
                    vm.SlotText = escaped;
                }
                vm.SlotBrush = ThemeManager.Current.String;
                break;
            case JsonValueKind.Number:
                vm.SlotText = raw;
                vm.SlotBrush = ThemeManager.Current.Number;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                vm.SlotText = raw;
                vm.SlotBrush = ThemeManager.Current.Bool;
                break;
            default: // Null、Undefined 等
                vm.SlotText = "null";
                vm.SlotBrush = ThemeManager.Current.Null;
                vm.SlotStyle = FontStyles.Italic;
                break;
        }
    }

    /// <summary>把字符串内容包上引号并转义控制字符，用于单行显示。</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>由该节点重建 JSON 文本（右键复制用），标量使用原始文本以保留数字精度。</summary>
    public string ToJsonText()
    {
        if (ScalarRaw is not null) return ScalarRaw;
        if (IsArray)
            return "[" + string.Join(",", Children.Select(c => c.ToJsonText())) + "]";
        return "{" + string.Join(",", Children.Select(c =>
            "\"" + System.Text.Json.JsonEncodedText.Encode(c.Key) + "\":" + c.ToJsonText())) + "}";
    }

    /// <summary>按深度规则设置默认展开：对象展开，数组折叠（根数组除外）。</summary>
    public void ApplyDefaultExpand(int depthLimit)
        => ApplyExpand(this, 0, depthLimit);

    private static void ApplyExpand(JsonNodeVM vm, int depth, int limit)
    {
        vm._isExpanded = depth < limit && (!vm.IsArray || depth == 0);
        foreach (var c in vm.Children)
            ApplyExpand(c, depth + 1, limit);
    }

    public void SetAllExpanded(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var c in Children)
            c.SetAllExpanded(expanded);
    }

    // ---------- 修改对比（与上一次解析基线） ----------

    /// <summary>上一次解析内容的快照：节点签名 + 各容器的子节点签名。</summary>
    public sealed class BaselineInfo
    {
        public readonly Dictionary<string, string> Sigs = new();                          // 路径 -> 节点值签名
        public readonly Dictionary<string, Dictionary<string, string>> ChildSigs = new(); // 路径 -> (子key -> 子签名)
    }

    /// <summary>字符串取解码后的内容（与 \uXXXX 转义形式无关），容器取元素个数。</summary>
    private static string ValueSigOf(JsonNodeVM n)
        => n.IsScalar ? n.ScalarCompare : "#" + n.Children.Count;

    /// <summary>识别改名用的节点签名：容器额外纳入子 key 列表。</summary>
    private static string KeySigOf(JsonNodeVM n)
        => n.IsScalar ? n.ScalarCompare : "#C" + n.Children.Count + "|" + string.Join(",", n.Children.Select(c => c.Key));

    public static BaselineInfo? BuildBaseline(JsonNodeVM? root)
    {
        if (root is null) return null;
        var b = new BaselineInfo();
        FillBaseline(root, "", b);
        return b;
    }

    private static void FillBaseline(JsonNodeVM n, string path, BaselineInfo b)
    {
        b.Sigs[path] = ValueSigOf(n);
        var childSigs = new Dictionary<string, string>();
        foreach (var c in n.Children)
        {
            childSigs[c.Key] = KeySigOf(c);
            FillBaseline(c, path.Length == 0 ? c.Key : path + '\u0001' + c.Key, b);
        }
        b.ChildSigs[path] = childSigs;
    }

    /// <summary>与基线对比并标红：改值→只红值；改名→只红 key；新增→key 和值都红。</summary>
    public void MarkDiff(BaselineInfo? baseline)
    {
        if (baseline is not null)
            Mark(this, null, "", baseline);
    }

    private static void Mark(JsonNodeVM vm, JsonNodeVM? parent, string path, BaselineInfo b)
    {
        if (path.Length > 0)
        {
            bool isNew = !b.Sigs.TryGetValue(path, out var oldSig);
            if (isNew)
            {
                vm.IsKeyModified = true;
                vm.KeyBrush = ThemeManager.Current.Modified;
                vm.KeyWeight = FontWeights.SemiBold;
                bool valueRed = true;
                // 改名识别：非数组下标的新 key，且同父容器下有“已消失的旧 key”签名与当前节点一致
                if (!vm.Key.StartsWith('[') && parent is not null &&
                    b.ChildSigs.TryGetValue(ParentPath(path), out var oldSibs))
                {
                    var newKeys = new HashSet<string>(parent.Children.Select(c => c.Key));
                    string curSig = KeySigOf(vm);
                    if (oldSibs.Any(kv => !newKeys.Contains(kv.Key) && kv.Value == curSig))
                        valueRed = false; // 只是改了 key 名，值没变 → 只红 key
                }
                if (valueRed)
                    MarkValueRed(vm);
            }
            else if (oldSig != ValueSigOf(vm))
            {
                MarkValueRed(vm);
            }
        }

        string prefix = path.Length == 0 ? "" : path + '\u0001';
        foreach (var c in vm.Children)
            Mark(c, vm, prefix + c.Key, b);
    }

    private static void MarkValueRed(JsonNodeVM vm)
    {
        vm.IsValueModified = true;
        vm.SlotBrush = ThemeManager.Current.Modified;
        vm.SlotWeight = FontWeights.SemiBold;
    }

    // ---------- 主题重着色 ----------

    /// <summary>主题切换后按类型/修改标记重着色整棵树。</summary>
    public void ApplyPaletteRecursive()
    {
        var p = ThemeManager.Current;
        KeyBrush = IsKeyModified ? p.Modified
            : IsRoot ? p.RootKey
            : Key.StartsWith('[') ? p.IndexKey : p.Key;

        if (IsValueModified)
        {
            SlotBrush = p.Modified;
        }
        else if (IsScalar)
        {
            SlotBrush = Kind switch
            {
                JsonValueKind.String => p.String,
                JsonValueKind.Number => p.Number,
                JsonValueKind.True or JsonValueKind.False => p.Bool,
                _ => p.Null,
            };
        }
        else if (IsArray || IsObject)
        {
            SlotBrush = p.Count;
        }

        foreach (var c in Children)
            c.ApplyPaletteRecursive();
    }

    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('\u0001');
        return i < 0 ? "" : path[..i];
    }
}

