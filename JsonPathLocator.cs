using System.Text;

namespace JsonFormatter;

/// <summary>
/// 把左侧源码中的光标位置映射为节点路径：找到包含该位置（或其前方最近）的
/// key / 值词法单元。允许语法未写完或有注释——扫描到出错处即停，返回已得结果。
/// </summary>
internal static class JsonPathLocator
{
    public static List<string>? FindPathAt(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret < 0 || caret > text.Length) return null;
        var ctx = new Ctx(text, caret);
        try
        {
            ScanValue(ctx, new List<string>());
        }
        catch (FoundException f)
        {
            return f.Path ?? ctx.LastBefore;
        }
        catch (ScanStopException)
        {
        }
        return ctx.LastBefore;
    }

    // ---------- 扫描上下文 ----------

    private sealed class Ctx
    {
        public Ctx(string text, int caret) { Text = text; Caret = caret; }
        public string Text;
        public int Caret;
        public int Pos;
        public List<string>? LastBefore; // 光标前最近的词法单元（兜底）
    }

    private sealed class FoundException : Exception
    {
        public FoundException(List<string> path) { Path = path; }
        public List<string>? Path { get; }
    }

    private sealed class ScanStopException : Exception
    {
        public static readonly ScanStopException Instance = new();
    }

    private static List<string> Append(List<string> path, string seg)
    {
        var p = new List<string>(path.Count + 1) { };
        p.AddRange(path);
        p.Add(seg);
        return p;
    }

    /// <summary>记录一个词法单元：包含光标则立即命中返回，否则记为光标前兜底。</summary>
    private static void Record(Ctx c, List<string> path, int start, int end)
    {
        if (c.Caret >= start && c.Caret < end)
            throw new FoundException(new List<string>(path));
        if (end <= c.Caret)
            c.LastBefore = new List<string>(path);
    }

    private static int SkipWs(Ctx c)
    {
        int i = c.Pos, n = c.Text.Length;
        while (i < n)
        {
            char ch = c.Text[i];
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') { i++; continue; }
            if (ch == '/' && i + 1 < n) // 容忍注释
            {
                if (c.Text[i + 1] == '/') { while (i < n && c.Text[i] != '\n') i++; continue; }
                if (c.Text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(c.Text[i] == '*' && c.Text[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, n);
                    continue;
                }
            }
            break;
        }
        c.Pos = i;
        return i;
    }

    /// <summary>返回字符串的结束引号下标（从开始引号起扫描）。</summary>
    private static int StringEnd(string s, int start)
    {
        int i = start + 1, n = s.Length;
        while (i < n)
        {
            char ch = s[i];
            if (ch == '\\') { i += 2; continue; }
            if (ch == '"') return i;
            i++;
        }
        throw ScanStopException.Instance;
    }

    /// <summary>数字或 true/false/null 字面量的结束位置（开区间）。</summary>
    private static int LiteralEnd(string s, int start)
    {
        int i = start, n = s.Length;
        while (i < n)
        {
            char ch = s[i];
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == ',' || ch == ':' || ch == ']' || ch == '}') break;
            i++;
        }
        return i;
    }

    // ---------- 递归下降 ----------

    private static void ScanValue(Ctx c, List<string> path)
    {
        int i = SkipWs(c);
        if (i >= c.Text.Length) throw ScanStopException.Instance;
        char ch = c.Text[i];
        switch (ch)
        {
            case '"':
            {
                int end = StringEnd(c.Text, i);
                Record(c, path, i, end + 1);
                c.Pos = end + 1;
                break;
            }
            case '{':
                ScanObject(c, path);
                break;
            case '[':
                ScanArray(c, path);
                break;
            default:
            {
                if (ch == '}' || ch == ']' || ch == ',') throw ScanStopException.Instance;
                int end = LiteralEnd(c.Text, i);
                if (end == i) throw ScanStopException.Instance;
                Record(c, path, i, end);
                c.Pos = end;
                break;
            }
        }
    }

    private static void ScanObject(Ctx c, List<string> path)
    {
        c.Pos++; // {
        while (true)
        {
            int i = SkipWs(c);
            if (i >= c.Text.Length) throw ScanStopException.Instance;
            char ch = c.Text[i];
            if (ch == '}') { c.Pos = i + 1; return; }
            if (ch == ',') { c.Pos = i + 1; continue; }
            if (ch != '"') throw ScanStopException.Instance;

            int keyEnd = StringEnd(c.Text, i);
            string key = Unescape(c.Text, i + 1, keyEnd);
            var propPath = Append(path, key);
            Record(c, propPath, i, keyEnd + 1); // 双击 key → 定位到该属性节点

            int j = SkipWsFrom(c, keyEnd + 1);
            if (j >= c.Text.Length || c.Text[j] != ':') throw ScanStopException.Instance;
            c.Pos = j + 1;
            ScanValue(c, propPath); // 值（无论标量还是容器）都挂在属性路径上
        }
    }

    private static void ScanArray(Ctx c, List<string> path)
    {
        c.Pos++; // [
        int idx = 0;
        while (true)
        {
            int i = SkipWs(c);
            if (i >= c.Text.Length) throw ScanStopException.Instance;
            char ch = c.Text[i];
            if (ch == ']') { c.Pos = i + 1; return; }
            if (ch == ',') { c.Pos = i + 1; idx++; continue; }
            ScanValue(c, Append(path, "[" + idx + "]"));
        }
    }

    private static int SkipWsFrom(Ctx c, int i)
    {
        c.Pos = i;
        return SkipWs(c);
    }

    private static string Unescape(string s, int start, int endQuote)
    {
        // 大多数 key 无转义，先快查
        int slice = endQuote - start;
        if (slice <= 0) return "";
        if (s.IndexOf('\\', start, slice) < 0) return s.Substring(start, slice);

        var sb = new StringBuilder(slice);
        for (int i = start; i < endQuote; i++)
        {
            char ch = s[i];
            if (ch != '\\' || i + 1 >= endQuote) { sb.Append(ch); continue; }
            char e = s[++i];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < endQuote && int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                    {
                        sb.Append((char)cp);
                        i += 4;
                    }
                    else sb.Append('u');
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }
}
