using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia.Media;

namespace JsonFormatter;

/// <summary>JSON 树节点。对象默认展开；数组折叠并以徽章标注数量。</summary>
public sealed class JsonNodeVM : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _inSubtree;
    private IBrush? _keyBrush;
    private IBrush? _slotBrush;
    private IBrush? _rowBg;
    private FontWeight _slotWeight = FontWeight.Normal;
    private FontStyle _slotStyle = FontStyle.Normal;

    public string Key { get; }
    public List<JsonNodeVM> Children { get; } = new();

    public bool IsArray { get; private set; }
    public bool IsObject { get; private set; }
    public bool IsRoot { get; private set; }
    public bool IsScalar => ScalarRaw is not null;
    public bool HasChildren => Children.Count > 0;

    public string? ScalarRaw { get; private set; }
    /// <summary>对比用规范值：字符串取解码内容（与 \uXXXX 无关）。</summary>
    public string ScalarCompare { get; private set; } = "";

    /// <summary>该子树是否含非空内容（隐藏空值时用于过滤）。</summary>
    public bool HasContent { get; private set; }

    public string? ValueText { get; private set; }
    public string? BadgeText { get; private set; }
    public bool BadgeVisible => BadgeText is not null;
    public bool HasValue => ValueText is not null;
    public bool ShowSep => ValueText is not null || BadgeText is not null;
    public string? SlotTooltip { get; private set; }
    public FontWeight KeyWeight { get; }

    /// <summary>原始 JSON 类型（主题切换后按类型重着色）。</summary>
    public JsonValueKind Kind { get; private set; }
    public bool IsKeyModified { get; private set; }
    public bool IsValueModified { get; private set; }

    public IBrush KeyBrush
    {
        get => _keyBrush!;
        private set { if (!ReferenceEquals(_keyBrush, value)) { _keyBrush = value; PC(nameof(KeyBrush)); } }
    }

    public IBrush SlotBrush
    {
        get => _slotBrush!;
        private set { if (!ReferenceEquals(_slotBrush, value)) { _slotBrush = value; PC(nameof(SlotBrush)); } }
    }

    public FontWeight SlotWeight
    {
        get => _slotWeight;
        private set { if (_slotWeight != value) { _slotWeight = value; PC(nameof(SlotWeight)); } }
    }

    public FontStyle SlotStyle
    {
        get => _slotStyle;
        private set { if (_slotStyle != value) { _slotStyle = value; PC(nameof(SlotStyle)); } }
    }

    /// <summary>行背景：选中深、子树浅、未选中透明（绑定驱动，防容器复用丢色）。</summary>
    public IBrush? RowBackground
    {
        get => _rowBg;
        private set { if (!ReferenceEquals(_rowBg, value)) { _rowBg = value; PC(nameof(RowBackground)); } }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; PC(nameof(IsExpanded)); } }
    }

    public bool IsSelectedNode
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; PC(nameof(IsSelectedNode)); RefreshRow(); } }
    }

    public bool IsSubtreeHighlighted
    {
        get => _inSubtree;
        set { if (_inSubtree != value) { _inSubtree = value; PC(nameof(IsSubtreeHighlighted)); RefreshRow(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void PC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private void RefreshRow()
    {
        var p = ThemeManager.Current;
        RowBackground = _isSelected ? p.SelBg : _inSubtree ? p.SubtreeBg : null;
    }

    private JsonNodeVM(string key, bool isRoot)
    {
        Key = key;
        IsRoot = isRoot;
        KeyWeight = isRoot ? FontWeight.Bold : FontWeight.Normal;
    }

    // ---------- 构建 ----------

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
        vm.KeyBrush = vm.IsRoot ? p.RootKey : vm.Key.StartsWith('[') ? p.IndexKey : p.Key;

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
                vm.BadgeText = vm.Children.Count == 0 && !hideEmpty ? "{}" : null;
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
                vm.BadgeText = $"[{total}]";
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
        string raw = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
        vm.ScalarRaw = e.GetRawText();
        vm.ScalarCompare = e.ValueKind == JsonValueKind.String ? "s:" + raw : vm.ScalarRaw;
        vm.HasContent = vm.ScalarRaw is not ("null" or "\"\"");

        var p = ThemeManager.Current;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                string escaped = Escape(raw);
                if (escaped.Length > 268)
                {
                    vm.ValueText = escaped[..264] + "…";
                    vm.SlotTooltip = escaped.Length > 5002 ? escaped[..5000] + "…" : escaped;
                }
                else vm.ValueText = escaped;
                vm.SlotBrush = p.String;
                break;
            case JsonValueKind.Number:
                vm.ValueText = raw;
                vm.SlotBrush = p.Number;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                vm.ValueText = raw;
                vm.SlotBrush = p.Bool;
                break;
            default:
                vm.ValueText = "null";
                vm.SlotBrush = p.Null;
                vm.SlotStyle = FontStyle.Italic;
                break;
        }
    }

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

    // ---------- 主题重着色 ----------

    public void ApplyPaletteRecursive()
    {
        var p = ThemeManager.Current;
        KeyBrush = IsKeyModified ? p.Modified
            : IsRoot ? p.RootKey
            : Key.StartsWith('[') ? p.IndexKey : p.Key;
        RefreshRow();

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

    // ---------- 展开 ----------

    public void ApplyDefaultExpand(int depthLimit) => ApplyExpand(this, 0, depthLimit);

    private static void ApplyExpand(JsonNodeVM vm, int depth, int limit)
    {
        vm.IsExpanded = depth < limit && (!vm.IsArray || depth == 0);
        foreach (var c in vm.Children)
            ApplyExpand(c, depth + 1, limit);
    }

    public void SetAllExpanded(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var c in Children)
            c.SetAllExpanded(expanded);
    }

    // ---------- 子树高亮 ----------

    public void ApplyHighlight()
    {
        IsSelectedNode = true;
        Mark(this);
        static void Mark(JsonNodeVM n)
        {
            n.IsSubtreeHighlighted = true;
            foreach (var c in n.Children)
                Mark(c);
        }
    }

    public void ClearHighlight()
    {
        if (!IsSelectedNode && !IsSubtreeHighlighted) return;
        IsSelectedNode = false;
        IsSubtreeHighlighted = false;
        foreach (var c in Children)
            c.ClearHighlight();
    }

    // ---------- 修改对比 ----------

    public sealed class BaselineInfo
    {
        public readonly Dictionary<string, string> Sigs = new();
        public readonly Dictionary<string, Dictionary<string, string>> ChildSigs = new();
    }

    private static string ValueSigOf(JsonNodeVM n)
        => n.IsScalar ? n.ScalarCompare : "#" + n.Children.Count;

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

    /// <summary>改值→只红值；改名→只红 key；新增→key+值红。</summary>
    public void MarkDiff(BaselineInfo? baseline)
    {
        if (baseline is not null)
            Mark(this, null, "", baseline);
    }

    private static void Mark(JsonNodeVM vm, JsonNodeVM? parent, string path, BaselineInfo b)
    {
        if (path.Length > 0)
        {
            var p = ThemeManager.Current;
            bool isNew = !b.Sigs.TryGetValue(path, out var oldSig);
            if (isNew)
            {
                vm.IsKeyModified = true;
                vm.KeyBrush = p.Modified;
                bool valueRed = true;
                if (!vm.Key.StartsWith('[') && parent is not null &&
                    b.ChildSigs.TryGetValue(ParentPath(path), out var oldSibs))
                {
                    var newKeys = new HashSet<string>(parent.Children.Select(c => c.Key));
                    string curSig = KeySigOf(vm);
                    if (oldSibs.Any(kv => !newKeys.Contains(kv.Key) && kv.Value == curSig))
                        valueRed = false; // 只改了 key 名
                }
                if (valueRed)
                {
                    vm.IsValueModified = true;
                    vm.SlotBrush = p.Modified;
                    vm.SlotWeight = FontWeight.Bold;
                }
            }
            else if (oldSig != ValueSigOf(vm))
            {
                vm.IsValueModified = true;
                vm.SlotBrush = p.Modified;
                vm.SlotWeight = FontWeight.Bold;
            }
        }

        string prefix = path.Length == 0 ? "" : path + '\u0001';
        foreach (var c in vm.Children)
            Mark(c, vm, prefix + c.Key, b);
    }

    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('\u0001');
        return i < 0 ? "" : path[..i];
    }

    // ---------- 导出 ----------

    public string ToJsonText()
    {
        if (ScalarRaw is not null) return ScalarRaw;
        if (IsArray)
            return "[" + string.Join(",", Children.Select(c => c.ToJsonText())) + "]";
        return "{" + string.Join(",", Children.Select(c =>
            "\"" + JsonEncodedText.Encode(c.Key) + "\":" + c.ToJsonText())) + "}";
    }
}
