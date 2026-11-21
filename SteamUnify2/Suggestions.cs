using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUnify;

public sealed record Suggestion(string Name, string Group, string[] Tags, bool NeedsReview, string Source)
{
    public bool NameLocked { get; init; }
    public bool GroupLocked { get; init; }
}

public sealed partial class Suggestions
{
    public static readonly string[] Groups =
        ["动作与射击", "角色扮演", "剧情与解谜", "策略与经营", "生存与沙盒", "竞速与体育", "休闲"];
    private sealed record SavedApp(string? ManualName = null, string? ManualGroup = null,
        string? OnlineName = null, string? OnlineSource = null, string? Description = null,
        string[]? ImportedTags = null);

    private readonly string preferencesPath;
    private readonly object gate = new();
    private readonly object saveGate = new();
    private static readonly SavedApp EmptyPreferences = new();
    private readonly Dictionary<int,SavedApp> apps;
    private readonly Dictionary<int,string> heyboxNames;
    private readonly Dictionary<int,string> names;
    private readonly Dictionary<int,string> tagNames;
    private readonly Dictionary<int,string> genres;
    private readonly CollectionRules rules;
    private readonly SuggestionLookup network;
    private bool dirty;

    public Suggestions(string dataDir) : this(dataDir, new SuggestionLookup()) { }

    internal Suggestions(string dataDir, SuggestionLookup network)
    {
        this.network = network;
        string resources = Path.Combine(AppContext.BaseDirectory, "Resources");
        heyboxNames = ReadMap(resources, "heybox_names.json");
        names = ReadMap(resources, "official_names.json");
        tagNames = ReadMap(resources, "tag_names_zh.json");
        genres = ReadMap(resources, "genres_zh.json");
        rules = new CollectionRules(Path.Combine(resources, "category_model.json"));
        preferencesPath = Path.Combine(dataDir, "suggestions.v2.json");
        if (File.Exists(preferencesPath))
        {
            apps = JsonSerializer.Deserialize<Dictionary<int,SavedApp>>(File.ReadAllBytes(preferencesPath))
                ?? throw new InvalidDataException("无法读取本地整理记录。");
        }
        else
        {
            apps = new();
            ImportLegacy(dataDir);
            Save();
        }
    }

    private static Dictionary<int,string> ReadMap(string folder, string file) =>
        JsonSerializer.Deserialize<Dictionary<int,string>>(File.ReadAllBytes(Path.Combine(folder, file)))
            ?? throw new InvalidDataException($"无法读取资源 {file}");

    public int ImportLegacyFrom(string oldDataDir)
    {
        if (!File.Exists(Path.Combine(oldDataDir, "state.json")) && !File.Exists(Path.Combine(oldDataDir, "descriptions.json")))
            throw new FileNotFoundException("此目录内没有旧版的 state.json 或 descriptions.json。请选择旧版的 data 目录。");
        int count = ImportLegacy(oldDataDir);
        Save();
        return count;
    }

    private int ImportLegacy(string dataDir)
    {
        var imported = new HashSet<int>();
        string path = Path.Combine(dataDir, "state.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("apps", out var records) && records.ValueKind == JsonValueKind.Object)
            foreach (var item in records.EnumerateObject())
            {
                if (!int.TryParse(item.Name, out int appId) || item.Value.ValueKind != JsonValueKind.Object) continue;
                var rec = item.Value;
                string? Field(string key) => rec.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim() : null;
                bool Flag(string key) => rec.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
                var legacy = new List<string>();
                if (rec.TryGetProperty("categories", out var array) && array.ValueKind == JsonValueKind.Array)
                    legacy.AddRange(array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)));
                if (legacy.Count == 0 && !string.IsNullOrWhiteSpace(Field("category"))) legacy.Add(Field("category")!);
                string? primary = Field("primary_category");
                if (string.IsNullOrEmpty(primary)) primary = legacy.FirstOrDefault();
                string? source = Field("source");
                string? name = Field("zh_name");
                bool manual = Flag("locked") || source is "manual" or "original";
                bool groupLocked = Flag("category_locked") || Field("category_source") == "manual";
                Update(appId, r => r with
                {
                    ManualName = r.ManualName ?? (manual ? (source == "original" ? "" : name ?? "") : null),
                    ManualGroup = r.ManualGroup ?? (groupLocked ? primary ?? "" : null),
                    OnlineName = r.OnlineName ?? (!manual && ContainsHan(name) ? name : null),
                    OnlineSource = r.OnlineSource ?? (source == "machine" ? "机器翻译" : "旧版译名"),
                    Description = r.Description ?? Field("description"),
                    ImportedTags = legacy.Concat(r.ImportedTags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                });
                imported.Add(appId);
            }
            lock (gate) dirty = true;
        }
        string descPath = Path.Combine(dataDir, "descriptions.json");
        if (!File.Exists(descPath)) return imported.Count;
        using var desc = JsonDocument.Parse(File.ReadAllBytes(descPath));
        if (desc.RootElement.ValueKind != JsonValueKind.Object) return imported.Count;
        foreach (var item in desc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(item.Name, out int appId)) continue;
            var value = item.Value;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("text", out var text)) value = text;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                Update(appId, r => r with { Description = r.Description ?? value.GetString() });
                imported.Add(appId);
            }
        }
        return imported.Count;
    }

    private SavedApp Get(int appId)
    {
        lock (gate) return apps.GetValueOrDefault(appId) ?? EmptyPreferences;
    }

    private void Update(int appId, Func<SavedApp,SavedApp> update)
    {
        lock (gate)
        {
            var old = apps.GetValueOrDefault(appId) ?? EmptyPreferences;
            var next = update(old);
            if (next == old) return;
            apps[appId] = next;
            dirty = true;
        }
    }

    internal static bool ContainsHan(string? text) => !string.IsNullOrEmpty(text) &&
        text.Any(c => c >= '\u3400' && c <= '\u9fff');

    private (string Name, string Source) ChooseName(CacheGame game, SavedApp prefs)
    {
        if (prefs.ManualName != null) return (prefs.ManualName, "手动");
        if (heyboxNames.TryGetValue(game.AppId, out var heyboxName)) return (heyboxName, "小黑盒");
        foreach (string key in new[] { "schinese", "sc_schinese" })
            if (game.Localized.TryGetValue(key, out var official) && ContainsHan(official))
                return (official.Trim(), "Steam 内置");
        if (names.TryGetValue(game.AppId, out var dictionaryName)) return (dictionaryName, "内置词典");
        if (prefs.OnlineName != null) return (prefs.OnlineName, prefs.OnlineSource ?? "联网译名");
        if (ContainsHan(game.Name) && !game.Name.Any(c => c >= '\u3040' && c <= '\u30ff'))
            return (game.Name, "原名");
        return ("", "");
    }

    public Suggestion Recommend(CacheGame game, IReadOnlyDictionary<int,string> tagNames, string? originalName = null) =>
        Recommend(game, tagNames, originalName, Get(game.AppId));

    private Suggestion Recommend(CacheGame game, IReadOnlyDictionary<int,string> tagNames, string? originalName, SavedApp prefs)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ruleTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int id in game.Tags)
        {
            string? localized = tagNames.GetValueOrDefault(id);
            string? bundled = this.tagNames.GetValueOrDefault(id);
            if (!string.IsNullOrWhiteSpace(bundled)) ruleTags.Add(bundled);
            if (!string.IsNullOrWhiteSpace(localized)) ruleTags.Add(localized);
        }
        tags.UnionWith(ruleTags);
        foreach (int genre in game.Genres)
            if (genres.TryGetValue(genre, out var genreName)) tags.Add(genreName);
        if (prefs.ImportedTags != null) tags.UnionWith(prefs.ImportedTags);
        if (ruleTags.Any(t => t.Contains("Rogue", StringComparison.OrdinalIgnoreCase))) tags.Add("肉鸽");
        var (name, source) = ChooseName(game, prefs);
        var classificationGame = string.IsNullOrEmpty(originalName) || game.Name == originalName ? game : game with { Name = originalName };
        string group = prefs.ManualGroup ?? rules.Classify(classificationGame, ruleTags, prefs.Description);
        bool needsReview = prefs.ManualName == null && (name.Length == 0 || source == "机器翻译")
            || prefs.ManualGroup == null && group.Length == 0;
        return new(name, group, tags.ToArray(), needsReview, source)
        {
            NameLocked = prefs.ManualName != null,
            GroupLocked = prefs.ManualGroup != null
        };
    }

    public void SetManual(int appId, string? name, string? group) => Update(appId, r => r with
    {
        ManualName = name?.Trim() ?? r.ManualName,
        ManualGroup = group?.Trim() ?? r.ManualGroup
    });

    public void ClearManual(int appId) => Update(appId, r => r with { ManualName = null, ManualGroup = null });

    public void RememberOnline(int appId, string name)
    {
        if (!ContainsHan(name)) return;
        Update(appId, r => r.OnlineName == name ? r : r with { OnlineName = name.Trim(), OnlineSource = "联网译名" });
    }

    public async Task<string?> LookupAsync(CacheGame game, string? proxy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = await LookupOfficialAsync(game,proxy,ct).ConfigureAwait(false);
        if (state.NeedsMachine)
        {
            var result = await network.ResolveMachineAsync(game.Name,proxy,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            RememberMachineResult(game,result);
        }
        string name = ChooseName(game,Get(game.AppId)).Name;
        return name.Length == 0 ? null : name;
    }

    private (bool Found,bool NeedsMachine) LookupState(CacheGame game)
    {
        var prefs = Get(game.AppId);
        bool found = ChooseName(game,prefs).Name.Length != 0;
        return (found,!found && prefs.ManualName == null);
    }

    private async Task<(bool Found,bool NeedsMachine)> LookupOfficialAsync(CacheGame game,string? proxy,CancellationToken ct)
    {
        var state = LookupState(game);
        if (!state.NeedsMachine) return state;
        var result = await network.ResolveStoreAsync(game.AppId,game.Name,proxy,ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Update(game.AppId, r => r with
        {
            OnlineName = result.Name ?? r.OnlineName,
            OnlineSource = result.Source ?? r.OnlineSource,
            Description = result.Description ?? r.Description
        });
        return LookupState(game);
    }

    private void RememberMachineResult(CacheGame game,LookupResult result)
    {
        if (result.Name == null) return;
        Update(game.AppId,r => r.ManualName != null || ChooseName(game,r).Name.Length != 0
            ? r : r with { OnlineName = result.Name, OnlineSource = result.Source });
    }

    public async Task<bool> RetrieveDescriptionAsync(CacheGame game, string? proxy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(Get(game.AppId).Description)) return true;
        var result = await network.DescriptionAsync(game.AppId, proxy, ct).ConfigureAwait(false);
        if (result == null) return false;
        Update(game.AppId, r => r with { Description = result });
        return true;
    }

    public void Save()
    {
        lock (saveGate)
        {
            Dictionary<int,SavedApp> snapshot;
            lock (gate)
            {
                if (!dirty) return;
                snapshot = new(apps);
                dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
                string tmp = preferencesPath + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(snapshot));
                File.Move(tmp, preferencesPath, true);
            }
            catch
            {
                lock (gate) dirty = true;
                throw;
            }
        }
    }
}
