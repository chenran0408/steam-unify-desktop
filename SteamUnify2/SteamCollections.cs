using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamUnify;

internal sealed partial class SteamCollections
{
    const string Prefix = "user-collections.";
    readonly string namespacePath, versionsPath, ownershipPath;
    readonly JsonArray records;
    readonly Dictionary<string, string> managed;
    readonly Dictionary<string, JsonObject> byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonObject> values = new(StringComparer.Ordinal);
    readonly HashSet<string> existingNames = new(StringComparer.OrdinalIgnoreCase);
    long version;
    readonly long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal SteamCollections(SteamLocation location, string appData)
    {
        var cloud = Path.Combine(location.Root, "userdata", location.Account, "config", "cloudstorage");
        namespacePath = Path.Combine(cloud, "cloud-storage-namespace-1.json");
        versionsPath = Path.Combine(cloud, "cloud-storage-namespaces.json");
        ownershipPath = SteamLibrary.OwnershipPath(location, appData);
        dynamicOwnerPath = SteamLibrary.DynamicOwnerPath(location, appData);
        dynamicOwner = File.Exists(dynamicOwnerPath) ? JsonSerializer.Deserialize<string>(File.ReadAllBytes(dynamicOwnerPath)) : null;
        records = ReadArray(namespacePath);
        managed = File.Exists(ownershipPath)
            ? JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllBytes(ownershipPath)) ?? throw new InvalidDataException("收藏集归属文件为空。")
            : new();
        if (!File.Exists(ownershipPath))
        {
            using var old = SteamLibrary.LegacyState(location,appData,true);
            if (old != null && old.RootElement.TryGetProperty("collections",out var prior))
                managed = SteamLibrary.LegacyOwners(location,appData,prior.Deserialize<Dictionary<string,string>>() ?? new());
        }
        foreach (var node in records)
        {
            if (node is not JsonArray { Count: 2 } pair || pair[1] is not JsonObject rec) continue;
            var key = pair[0]?.ToString() ?? "";
            if (long.TryParse(rec["version"]?.ToString(), out long v)) version = Math.Max(version, v);
            if (!key.StartsWith(Prefix, StringComparison.Ordinal) || rec["is_deleted"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true) continue;
            if (rec["value"] is not JsonValue encoded || !encoded.TryGetValue<string>(out var raw)) continue;
            if (JsonNode.Parse(raw) is not JsonObject value) continue;
            existingNames.Add(value["name"]?.ToString() ?? "");
            var id = key[Prefix.Length..];
            if (id is "favorite" or "hidden") continue;
            if (value.ContainsKey("filterSpec"))
            {
                ReadAllGamesCandidate(id, rec, value);
                continue;
            }
            byId[id] = rec;
            values[id] = value;
        }
    }

    static JsonArray ReadArray(string path) => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? throw new InvalidDataException($"{Path.GetFileName(path)} 的格式应为数组。")
        : new();

    static HashSet<int> Ids(JsonNode? arr) =>
        arr is JsonArray items ? items.Select(n => int.Parse(n?.ToString() ?? "")).ToHashSet() : new();

    static JsonArray IdArray(HashSet<int> values)
        => new(values.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    internal Dictionary<int, List<string>> Memberships()
    {
        Dictionary<int, List<string>> result = new();
        foreach (var (name,id) in managed)
        {
            if (!values.TryGetValue(id, out var value)) continue;
            foreach (var appId in Ids(value["added"]))
            {
                if (!result.TryGetValue(appId, out var cats)) result[appId] = cats = new();
                cats.Add(name);
            }
        }
        return result;
    }

    internal void Plan(IReadOnlyList<LibraryEdit> edits, Dictionary<string, byte[]?> writes)
    {
        var assignments = edits.Where(e => e.Collection != null).ToDictionary(e => e.AppId, e => e.Collection!.Trim());
        if (assignments.Count == 0) return;
        var versions = ReadVersions(out var nsVersion);
        bool changed = false;
        bool ownersChanged = false;

        foreach (string name in managed.Keys.Concat(assignments.Values.Where(c => c.Length > 0)).Distinct().ToArray())
        {
            managed.TryGetValue(name, out string? id);
            bool live = id != null && values.ContainsKey(id);
            var wanted = assignments.Where(pair => pair.Value == name).Select(pair => pair.Key).ToArray();
            if (!live && wanted.Length == 0) continue;
            JsonObject rec;
            JsonObject value;
            if (live)
            {
                rec = byId[id!];
                value = values[id!];
            }
            else
            {
                id = "uc-" + Guid.NewGuid().ToString("N")[..16];
                string key = Prefix + id;
                string label = name;
                for (int suffix = 1; !existingNames.Add(label); suffix++)
                    label = suffix == 1 ? $"{name} · 统译" : $"{name} · 统译 {suffix}";
                value = new JsonObject { ["id"] = id, ["name"] = label };
                rec = new JsonObject { ["key"] = key, ["conflictResolutionMethod"] = "custom", ["strMethodId"] = "union-collections" };
                records.Add(new JsonArray(JsonValue.Create(key), rec));
                managed[name] = id;
                ownersChanged = true;
            }

            var before = Ids(value["added"]);
            var after = new HashSet<int>(before);
            after.ExceptWith(assignments.Keys);
            after.UnionWith(wanted);
            var removed = Ids(value["removed"]);
            var oldRemoved = new HashSet<int>(removed);
            removed.UnionWith(before.Except(after));
            removed.ExceptWith(wanted);
            if (live && before.SetEquals(after) && removed.SetEquals(oldRemoved)) continue;
            if (after.Count == 0)
            {
                var key = rec["key"]?.ToString() ?? Prefix + id;
                rec.Clear();
                rec["key"] = key;
                rec["is_deleted"] = true;
                managed.Remove(name);
                ownersChanged = true;
            }
            else
            {
                value["added"] = IdArray(after);
                value["removed"] = IdArray(removed);
                rec["value"] = value.ToJsonString();
            }
            rec["timestamp"] = now;
            rec["version"] = (++version).ToString();
            changed = true;
        }
        if (!changed) return;
        WriteVersions(versions,nsVersion,writes);
        if (ownersChanged) writes[ownershipPath] = JsonSerializer.SerializeToUtf8Bytes(managed);
    }

    internal void RemoveManaged(Dictionary<string,byte[]?> writes)
    {
        if (managed.Count == 0 && dynamicOwner == null) return;
        var versions = ReadVersions(out var nsVersion);
        bool changed = false;
        void Remove(string id, JsonObject rec)
        {
            rec.Clear();
            rec["key"] = Prefix + id;
            rec["is_deleted"] = true;
            rec["timestamp"] = now;
            rec["version"] = (++version).ToString();
            changed = true;
        }
        foreach (string id in managed.Values.Distinct())
            if (byId.TryGetValue(id,out var rec)) Remove(id,rec);
        if (dynamicOwner != null)
        {
            if (ownedDynamicRecord != null) Remove(dynamicOwner,ownedDynamicRecord);
            writes[dynamicOwnerPath] = null;
        }
        if (changed) WriteVersions(versions,nsVersion,writes);
        if (managed.Count > 0)
        {
            managed.Clear();
            writes[ownershipPath] = JsonSerializer.SerializeToUtf8Bytes(managed);
        }
    }

    private JsonArray ReadVersions(out JsonArray? nsVersion)
    {
        var versions = ReadArray(versionsPath);
        nsVersion = versions.OfType<JsonArray>().FirstOrDefault(a => a.Count == 2 && a[0]?.ToString() == "1");
        if (long.TryParse(nsVersion?[1]?.ToString(),out long oldVersion)) version = Math.Max(version,oldVersion);
        return versions;
    }

    private void WriteVersions(JsonArray versions,JsonArray? nsVersion,Dictionary<string,byte[]?> writes)
    {
        if (nsVersion == null) versions.Add(new JsonArray(JsonValue.Create(1),JsonValue.Create(version.ToString())));
        else nsVersion[1] = version.ToString();
        writes[namespacePath] = JsonSerializer.SerializeToUtf8Bytes(records);
        writes[versionsPath] = JsonSerializer.SerializeToUtf8Bytes(versions);
    }
}
