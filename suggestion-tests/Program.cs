using System.Text.Json;
using SteamUnify;

var scratch = Directory.CreateTempSubdirectory("suggestion-tests-");
try
{
    var tags = new Dictionary<int,string> { [1000001] = "第一人称射击", [1000002] = "Roguelite", [1000003] = "多 人" };
    var game = new CacheGame(9000000, "Test Game", "game", new(), [1], [1000001,1000002]);
    var fallbackStats = new List<string>();
    var fake = new SuggestionLookup((uri, proxy, token) =>
    {
        token.ThrowIfCancellationRequested();
        fallbackStats.Add(uri.Host);
        string json = uri.Host switch
        {
            "store.steampowered.com" =>
                """{"9000000":{"success":true,"data":{"name":"Test Game","short_description":"农场经营"}}}""",
            "store.steamchina.com" => """{"9000000":{"success":false}}""",
            _ => """{"quotaFinished":false,"responseStatus":200,"responseData":{"translatedText":"测试游戏"}}"""
        };
        return Task.FromResult<JsonElement?>(JsonSerializer.Deserialize<JsonElement>(json));
    });
    var engine = new Suggestions(scratch.FullName, fake);
    var offline = engine.Recommend(game, tags);
    Require(offline.Group == "动作与射击" && offline.Tags.Contains("肉鸽"), "primary and modifiers");
    Require(offline.NeedsReview && offline.Name == "", "missing name");
    var unknown = engine.Recommend(game with { Genres = [23,29], Tags = [] }, tags);
    Require(unknown.Group == "" && unknown.Tags.Contains("独立"), "uncertain group");
    string Group(int[] genres, params string[] labels)
    {
        var map = labels.Select((label,i) => (id:800000+i,label)).ToDictionary(p => p.id, p => p.label);
        return engine.Recommend(game with { Genres = genres.ToList(), Tags = map.Keys.ToList() },map).Group;
    }
    Require(Group([3], "日系角色扮演") == "角色扮演", "role playing");
    Require(Group([25], "视觉小说") == "剧情与解谜", "visual novel");
    Require(Group([3,23,28], "农场模拟", "生活模拟", "角色扮演") == "策略与经营", "farm simulation");
    Require(Group([1,3,25], "沙盒", "生存", "开放世界生存制作") == "生存与沙盒", "survival");
    Require(Group([9,18], "汽车模拟") == "竞速与体育", "sports");
    Require(Group([4], "放松", "休闲") == "休闲", "casual");
    string? online = await engine.LookupAsync(game, null, default);
    Require(online == "测试游戏", "translation fallback");
    Require(fallbackStats.SequenceEqual(["store.steampowered.com","store.steamchina.com","api.mymemory.translated.net"]), "source priority");
    Require(engine.Recommend(game, tags).Source == "机器翻译", "provenance");
    var official = engine.Recommend(game with { Localized = new() { ["schinese"] = "官方名称" } },tags);
    Require(official.Name == "官方名称" && official.Source == "Steam 内置", "official overrides cached machine result");
    var cyberpunk = game with { AppId = 1091500, Name = "Cyberpunk 2077",
        Localized = new() { ["schinese"] = "赛博朋克 2077" } };
    var box = engine.Recommend(cyberpunk, tags);
    Require(box.Name == "赛博朋克2077" && box.Source == "小黑盒", "HeyBox overrides Steam's localized title");
    var choices = engine.NameChoices(cyberpunk);
    Require(choices[0].Name == box.Name && choices[0].Source.Contains("小黑盒") &&
        choices.Any(choice=>choice.Name=="赛博朋克 2077" && choice.Source.Contains("Steam 内置")) &&
        choices.Select(choice=>choice.Name).Distinct().Count()==choices.Count,"candidate list shows conflicting sources once per title");
    engine.SetManual(cyberpunk.AppId, "", null);
    Require(engine.Recommend(cyberpunk, tags) is { Name: "", NameLocked: true }, "manual name skip overrides HeyBox");
    engine.ClearManual(cyberpunk.AppId);
    Require(await engine.LookupAsync(cyberpunk, null, default) == box.Name && fallbackStats.Count == 3,
        "bundled HeyBox title resolves without network lookup");
    Require(await engine.RetrieveDescriptionAsync(game,null,default) && fallbackStats.Count == 3, "description reuse");
    int requests = 0;
    var china = new SuggestionLookup((uri, proxy, token) =>
    {
        Require(proxy == "http://localhost:7890", "proxy passed through");
        requests++;
        var raw = uri.Host == "store.steamchina.com" ?
            """{"1":{"success":true,"data":{"name":"蒸汽平台官方名"}}}""" :
            """{"1":{"success":false}}""";
        return Task.FromResult<JsonElement?>(JsonSerializer.Deserialize<JsonElement>(raw));
    });
    var fromChina = await china.ResolveStoreAsync(1, "Original", "http://localhost:7890", default);
    Require(fromChina.Name == "蒸汽平台官方名" && requests == 2, "China store fallback before machine");
    engine.SetManual(game.AppId, "手动", "我的精选");
    engine.Save();
    var loaded = new Suggestions(scratch.FullName, fake);
    var manual = loaded.Recommend(game,tags);
    Require(manual.NameLocked && manual.GroupLocked && manual.Name == "手动" && manual.Group == "我的精选", "durable customizations");
    loaded.SetManual(game.AppId, "", "");
    var opted = loaded.Recommend(game,tags);
    Require(opted.NameLocked && opted.GroupLocked && !opted.NeedsReview && opted.Group == "", "explicit opt-out");
    loaded.ClearManual(game.AppId);
    Require(loaded.Recommend(game,tags).Name == "测试游戏", "release overrides");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await engine.LookupAsync(game,null,cancellation.Token); throw new Exception("Ignored cancellation"); }
    catch (OperationCanceledException) { }

    var migratedDir = Directory.CreateDirectory(Path.Combine(scratch.FullName,"old"));
    File.WriteAllText(Path.Combine(migratedDir.FullName,"state.json"),
        """{"apps":{"9000000":{"source":"manual","zh_name":"旧名","category_locked":true,"categories":["平台跳跃","多人"]}}}""");
    var migrated = new Suggestions(migratedDir.FullName, fake);
    var legacy = migrated.Recommend(game,tags);
    Require(legacy.NameLocked && legacy.Name == "旧名" && legacy.GroupLocked && legacy.Group == "平台跳跃" && legacy.Tags.Contains("多人"), "legacy records");
    migrated.ClearManual(game.AppId);
    migrated.Save();
    Require(!new Suggestions(migratedDir.FullName).Recommend(game,tags).GroupLocked, "migration runs once");
    loaded.SetManual(game.AppId, "新版手改", null);
    Require(loaded.ImportLegacyFrom(migratedDir.FullName) == 1, "explicit old-folder import count");
    var merged = new Suggestions(scratch.FullName).Recommend(game,tags);
    Require(merged.Name == "新版手改" && merged.Group == "平台跳跃", "import preserves existing overrides");
    await BatchChecks.Run(scratch.FullName, args.Contains("--benchmark"));
    if (args.Contains("--live"))
    {
        var real = new Suggestions(Path.Combine(scratch.FullName, "live"));
        var publicGame = game with { AppId = 1030300, Name = "Hollow Knight: Silksong", Localized = new(), Genres = [], Tags = [] };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        string? answer = await real.LookupAsync(publicGame, null, limit.Token);
        Console.WriteLine($"Live AppID {publicGame.AppId}, name={answer ?? "(missing)"}, source={real.Recommend(publicGame,tags).Source}");
    }
    Console.WriteLine("Suggestion tests passed.");
}
finally { scratch.Delete(recursive:true); }

static void Require(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}

namespace SteamUnify
{
    public sealed record CacheGame(int AppId,string Name,string Type,Dictionary<string,string> Localized,List<int> Genres,List<int> Tags);
}
