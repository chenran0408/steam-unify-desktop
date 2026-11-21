using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamUnify;

internal sealed class SteamVdf
{
    private readonly Dictionary<string, SteamVdf> children = new(StringComparer.OrdinalIgnoreCase);
    public string Text { get; private init; } = "";
    private int Start { get; init; }
    private int End { get; init; }
    public IEnumerable<KeyValuePair<string, SteamVdf>> Items => children;
    public SteamVdf? this[string key] => children.GetValueOrDefault(key);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SteamVdf? Open(string path) => File.Exists(path) ? Parse(File.ReadAllText(path, Utf8)) : null;

    internal static SteamVdf Parse(string text)
    {
        var root = new SteamVdf();
        var groups = new Stack<SteamVdf>();
        groups.Push(root);
        string? key = null;
        int p = 0;
        while (p < text.Length)
        {
            char c = text[p];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { p++; continue; }
            if (c == '/' && p + 1 < text.Length && text[p + 1] == '/')
            {
                int next = text.IndexOf('\n', p + 2);
                p = next < 0 ? text.Length : next + 1;
                continue;
            }
            if (c == '{')
            {
                if (key == null) throw new InvalidDataException("Steam VDF 数据缺少字段名。");
                var obj = new SteamVdf();
                groups.Peek().children[key] = obj;
                groups.Push(obj);
                key = null;
                p++;
                continue;
            }
            if (c == '}')
            {
                if (key != null || groups.Count == 1) throw new InvalidDataException("Steam VDF 括号不匹配。");
                groups.Pop();
                p++;
                continue;
            }
            int start = p;
            string token;
            if (c == '"')
            {
                p++;
                var value = new StringBuilder();
                bool closed = false;
                while (p < text.Length)
                {
                    char ch = text[p++];
                    if (ch == '"') { closed = true; break; }
                    if (ch == '\\' && p < text.Length)
                    {
                        ch = text[p++];
                        switch (ch)
                        {
                            case 'n': ch = '\n'; break;
                            case 'r': ch = '\r'; break;
                            case 't': ch = '\t'; break;
                            case '\\': case '"': break;
                            default: value.Append('\\'); break;
                        }
                    }
                    value.Append(ch);
                }
                if (!closed) throw new InvalidDataException("Steam VDF 字符串不完整。");
                token = value.ToString();
            }
            else
            {
                while (p < text.Length && !char.IsWhiteSpace(text[p]) && text[p] is not ('{' or '}' or '"')) p++;
                token = text[start..p];
            }
            if (key == null) key = token;
            else
            {
                groups.Peek().children[key] = new SteamVdf { Text = token, Start = start, End = p };
                key = null;
            }
        }
        if (groups.Count != 1 || key != null) throw new InvalidDataException("Steam VDF 数据不完整。");
        return root;
    }

    internal static byte[]? RenameManifest(string path, int appId, string name, out string originalName)
    {
        var bytes = File.ReadAllBytes(path);
        string text = Utf8.GetString(bytes);
        var state = Parse(text)["AppState"];
        if (!int.TryParse(state?["appid"]?.Text, out int found) || found != appId)
            throw new InvalidDataException($"游戏清单 {path} 的 AppID 不匹配。");
        var field = state?["name"] ?? throw new InvalidDataException($"游戏清单 {path} 缺少名称。");
        originalName = field.Text;
        if (field.Text == name) return null;
        string escaped = name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return Utf8.GetBytes(text[..field.Start] + "\"" + escaped + "\"" + text[field.End..]);
    }
}
