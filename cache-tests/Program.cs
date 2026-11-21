using SteamUnify;
using System.Text.Json;
var root = Path.GetFullPath(args[0]);
var warnings = new List<string>();
var before = SteamCache.Read(root, warnings);
if (args.Length == 1)
{
    Console.WriteLine($"Scanned {before.Count} entries with {warnings.Count} warnings");
    return;
}
int id = int.Parse(args[1]);
var game = before.Single(g => g.AppId == id);
var subset = SteamCache.Read(root,new(),new HashSet<int> { id, -1 });
if (subset.Count != 1 || JsonSerializer.Serialize(subset[0]) != JsonSerializer.Serialize(game) ||
    SteamCache.Read(root,new(),new HashSet<int>()).Count != 0) throw new Exception("Scoped metadata disagrees with the full cache.");
var changes = new Dictionary<int, NameChange> { [id] = new("本地化验证", null) };
var updates = SteamCache.PlanNames(root, changes);
if (updates.Count != 1) throw new Exception("Unexpected changed file count.");
foreach (var pair in updates) File.WriteAllBytes(pair.Key, pair.Value);
var after = SteamCache.Read(root, new());
var changed = after.Single(g => g.AppId == id);
if (before.Count != after.Count || changed.Localized.Count != 0 || changed.Name != changes[id].Name ||
    !game.Genres.SequenceEqual(changed.Genres) || !game.Tags.SequenceEqual(changed.Tags)) throw new Exception("Metadata changed.");
if (SteamCache.PlanNames(root, changes).Count != 0) throw new Exception("Idempotence failed.");
foreach (var pair in updates) File.Copy(pair.Key, pair.Key + ".actual", true);
changes[id] = new NameChange(game.Name, game.Localized);
foreach (var pair in SteamCache.PlanNames(root, changes)) File.WriteAllBytes(pair.Key, pair.Value);
var restored = SteamCache.Read(root, new()).Single(g => g.AppId == id);
if (restored.Name != game.Name || !game.Localized.OrderBy(x => x.Key).SequenceEqual(restored.Localized.OrderBy(x => x.Key)))
    throw new Exception("Localized restoration failed.");
if (!before.Select(g => g.AppId).SequenceEqual(after.Select(g => g.AppId))) throw new Exception("AppIds changed.");
Console.WriteLine($"Read, change, idempotence and restore passed for {Path.GetFileName(root)}");
