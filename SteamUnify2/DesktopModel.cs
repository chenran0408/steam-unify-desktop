using System.ComponentModel;
using System.IO;
using System.Text.Json;
namespace SteamUnify;

public sealed record DesktopOptions(string Root = "", string Account = "", bool AllCached = false,
    bool IncludeTools = false, bool RestartSteam = true, string Proxy = "", bool AutoLookup = false)
{
    public static DesktopOptions Load(string folder)
    {
        var path = Path.Combine(folder, "desktop.json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<DesktopOptions>(File.ReadAllText(path))
                ?? throw new InvalidDataException("设置文件内容为空。");
        string legacy = Path.Combine(folder, "settings.json");
        if (!File.Exists(legacy)) return new();
        using var json = JsonDocument.Parse(File.ReadAllText(legacy));
        var data = json.RootElement;
        string Text(string key) => data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Flag(string key, bool fallback) => data.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
        return new(Text("steam_root"), Text("account"), Flag("all_known", false), Flag("include_software", false),
            Flag("restart_steam", true), Text("proxy"), Flag("auto_translate_new", true) && Flag("use_network", true));
    }
    public void Save(string folder)
    {
        string path = Path.Combine(folder, "desktop.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public sealed class GameRow : INotifyPropertyChanged
{
    public required CacheGame Game { get; set; }
    public required string OriginalName { get; init; }
    public required Suggestion Suggested { get; set; }
    public required IReadOnlyList<string> CurrentGroups { get; set; }
    public bool Installed { get; init; }
    public int Id => Game.AppId;
    public string IdLabel => OriginalName != Game.Name ? $"{OriginalName}   ·   {Id}" :
        Installed ? $"AppID {Id}   ·   已安装" : $"AppID {Id}";
    public string Name => Suggested.Name;
    public string Group => Suggested.Group;
    public string SelectLabel => "勾选 " + Game.Name;
    public string DisplayName => Name.Length > 0 ? Name : (Suggested.NameLocked ? "已跳过" : "待补译");
    public string DisplayGroup => Group.Length > 0 ? Group : (Suggested.GroupLocked ? "已跳过" : "待选择");
    public string SourceLabel => Suggested.NameLocked && Name.Length == 0 ? "跳过改名" :
        Suggested.Source.Length == 0 ? "待补译" : Suggested.Source;
    public bool HasNameChange => Name.Length > 0 && Name != Game.Name;
    public bool HasGroupChange => Group.Length > 0 && (CurrentGroups.Count != 1 || CurrentGroups[0] != Group);
    public bool NeedsName => !Suggested.NameLocked && Name.Length == 0;
    public bool NeedsGroup => !Suggested.GroupLocked && Group.Length == 0;
    public bool MachineReview => !Suggested.NameLocked && Suggested.Source == "机器翻译";
    public bool HasChanges(bool names, bool groups) => (names && HasNameChange) || (groups && HasGroupChange);
    public bool HasDefaultChanges(bool names, bool groups) => HasChanges(names && !MachineReview, groups);
    public string Status => (NeedsName, MachineReview, NeedsGroup) switch
    {
        (true, _, true) => "缺译名和分类",
        (true, _, _) => "缺中文名",
        (_, true, true) => "核对机翻和分类",
        (_, true, _) => "核对机翻",
        (_, _, true) => "待选收藏集",
        _ => HasNameChange || HasGroupChange ? "待应用" : "无更改"
    };
    private string? searchText;
    public string SearchText => searchText ??= $"{Game.Name} {OriginalName} {Id} {Name} {Group} {string.Join(" ", Suggested.Tags)} {string.Join(" ",CurrentGroups)}";
    private bool picked = true;
    public bool Picked
    {
        get => picked;
        set { if (picked == value) return; picked = value; PropertyChanged?.Invoke(this, new(nameof(Picked))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(Suggestion suggestion)
    {
        Suggested=suggestion;
        searchText=null;
        PropertyChanged?.Invoke(this,new(string.Empty));
    }
}
public sealed record LibraryLink(string Key, string Name, string Glyph, int Count);
public sealed record ChangeLine(GameRow Row, LibraryEdit Edit)
{
    public string Title => Row.Game.Name;
    public string NameEdit => Edit.Name ?? "";
    public string GroupEdit => Edit.Collection switch
    {
        null => "",
        "" => $"移出 {string.Join("、", Row.CurrentGroups)}",
        var target when Row.CurrentGroups.Count == 0 => $"加入 {target}",
        var target => $"{string.Join("、", Row.CurrentGroups)} → {target}"
    };
}

