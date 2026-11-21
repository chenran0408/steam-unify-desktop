using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SteamUnify;

public static partial class SteamLibrary
{
    private sealed record OriginalName(NameChange? Cache, string? ManifestName);
    private static string OriginalsPath(SteamLocation loc,string data) => Path.Combine(Canon(data),$"originals-{RootKey(loc.Root)}.json");

    internal static JsonDocument? LegacyState(SteamLocation loc,string data,bool sameAccount)
    {
        string old = Path.Combine(data,"state.json");
        if (!File.Exists(old)) return null;
        string settingsFile = Path.Combine(data,"settings.json");
        if (File.Exists(settingsFile))
        {
            using var settings = JsonDocument.Parse(File.ReadAllBytes(settingsFile));
            if (settings.RootElement.TryGetProperty("steam_root",out var r) && r.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(r.GetString()) && !Paths.Equals(Canon(r.GetString()!),Canon(loc.Root))) return null;
        }
        var doc = JsonDocument.Parse(File.ReadAllBytes(old));
        if (doc.RootElement.TryGetProperty("account",out var a) && uint.TryParse(a.ToString(),out var account)
            && Directory.Exists(Path.Combine(loc.Root,"userdata",account.ToString())) && (!sameAccount || account.ToString() == loc.Account))
            return doc;
        doc.Dispose();
        return null;
    }

    // Old versions also claimed existing same-name collections. Accept legacy ownership only when
    // a pre-apply snapshot shows that this ID did not exist before the tool began managing it.
    internal static Dictionary<string,string> LegacyOwners(SteamLocation loc,string data,Dictionary<string,string> old)
    {
        var candidates = new Dictionary<string,string>(old);
        var result = new Dictionary<string,string>();
        if (candidates.Count == 0) return result;
        foreach (string backup in Backups(loc,data).Where(d => File.Exists(Path.Combine(d,"manifest.json"))))
        {
            using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(backup,"manifest.json")));
            var then = meta.RootElement.TryGetProperty("managed_collections",out var m)
                ? m.Deserialize<Dictionary<string,string>>() ?? new() : new();
            string ns = Path.Combine(backup,"userdata",loc.Account,"config","cloudstorage","cloud-storage-namespace-1.json");
            if (!File.Exists(ns)) continue;
            using var oldNs = JsonDocument.Parse(File.ReadAllBytes(ns));
            if (oldNs.RootElement.ValueKind != JsonValueKind.Array) continue;
            var ids = oldNs.RootElement.EnumerateArray()
                .Where(pair => pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                .Select(pair => pair[0].ToString()).ToHashSet();
            foreach (var (name,id) in candidates.ToArray())
            {
                if (then.ContainsValue(id)) continue;
                if (!ids.Contains("user-collections." + id)) result.Add(name,id);
                candidates.Remove(name);
            }
            if (candidates.Count == 0) break;
        }
        return result;
    }

    private static Dictionary<int,OriginalName> ReadOriginals(SteamLocation loc,string data,
        IReadOnlyDictionary<int,CacheGame>? live = null)
    {
        string path = OriginalsPath(loc,data);
        if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<int,OriginalName>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("原始名称记录为空。");
        using var state = LegacyState(loc,data,false);
        var originals = new Dictionary<int,OriginalName>();
        if (state == null || !state.RootElement.TryGetProperty("apps",out var apps)) return originals;
        live ??= SteamCache.Read(loc.Root,new()).ToDictionary(g=>g.AppId);
        var installed = FindInstalled(loc.Root);
        foreach (var prop in apps.EnumerateObject())
        {
            var rec = prop.Value;
            if (!int.TryParse(prop.Name,out int id) || !live.TryGetValue(id,out var game) || rec.ValueKind != JsonValueKind.Object ||
                !rec.TryGetProperty("applied",out var applied) || applied.ValueKind != JsonValueKind.True ||
                !rec.TryGetProperty("zh_name",out var translated) || translated.ToString() != game.Name ||
                !rec.TryGetProperty("orig_name",out var original) || string.IsNullOrWhiteSpace(original.ToString())) continue;
            var localized = rec.TryGetProperty("orig_localized",out var map) && map.ValueKind == JsonValueKind.Object ?
                map.Deserialize<Dictionary<string,string>>() : null;
            string? acf = null;
            if (installed.TryGetValue(id,out var manifest) && SteamVdf.Open(manifest)?["AppState"]?["name"]?.Text == game.Name)
                acf = original.ToString();
            originals[id] = new(new(original.ToString(),localized),acf);
        }
        return originals;
    }

    public static Dictionary<int,string> OriginalNames(SteamLocation location,string appData,IReadOnlyDictionary<int,CacheGame> live) =>
        ReadOriginals(location,appData,live)
            .Where(pair => pair.Value.Cache != null && live.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Cache!.Name);

    private static void PlanTrackedNames(SteamLocation loc,string data,Dictionary<int,NameChange> names,Dictionary<string,byte[]?> writes)
    {
        var captured = new Dictionary<int,CacheGame>();
        foreach (var (path,bytes) in SteamCache.PlanNames(loc.Root,names,captured)) writes.Add(path,bytes);
        var installed = FindInstalled(loc.Root);
        var acfNames = new Dictionary<int,string>();
        foreach (var (id,target) in names)
        {
            if (!installed.TryGetValue(id,out var path)) continue;
            var bytes = SteamVdf.RenameManifest(path,id,target.Name,out var previous);
            if (bytes == null) continue;
            acfNames[id] = previous;
            writes.Add(path,bytes);
        }
        if (captured.Count == 0 && acfNames.Count == 0) return;
        var ledger = ReadOriginals(loc,data);
        foreach (int id in captured.Keys.Concat(acfNames.Keys).Distinct())
        {
            ledger.TryGetValue(id,out var previous);
            var cache = previous?.Cache;
            if (cache == null && captured.TryGetValue(id,out var original)) cache = new(original.Name,original.Localized);
            ledger[id] = new(cache,previous?.ManifestName ?? acfNames.GetValueOrDefault(id));
        }
        writes[OriginalsPath(loc,data)] = JsonSerializer.SerializeToUtf8Bytes(ledger);
    }

    public static string UndoManaged(SteamLocation location,string appData,bool restartSteam)
    {
        SteamCache.Snapshot? snapshot = null;
        return WithClosedClient(location,restartSteam,() =>
        {
            var writes = new Dictionary<string,byte[]?>(Paths);
            snapshot?.RecoverMissing();
            var originals = ReadOriginals(location,appData);
            if (originals.Count != 0)
            {
                var current = SteamCache.Read(location.Root,new()).Select(g => g.AppId).ToHashSet();
                var names = originals.Where(e => e.Value.Cache != null && current.Contains(e.Key))
                    .ToDictionary(e => e.Key,e => e.Value.Cache!);
                foreach (var (path,bytes) in SteamCache.PlanNames(location.Root,names)) writes.Add(path,bytes);
                var installed = FindInstalled(location.Root);
                foreach (var (id,original) in originals.ToArray())
                {
                    if (original.ManifestName != null && installed.TryGetValue(id,out var path))
                    {
                        var file = SteamVdf.RenameManifest(path,id,original.ManifestName,out _);
                        if (file != null) writes.Add(path,file);
                    }
                    var remainingCache = current.Contains(id) ? null : original.Cache;
                    // Steam no longer uses an uninstalled game's old ACF.
                    if (remainingCache == null) originals.Remove(id);
                    else originals[id] = new(remainingCache,null);
                }
                writes[OriginalsPath(location,appData)] = JsonSerializer.SerializeToUtf8Bytes(originals);
            }
            new SteamCollections(location,appData).RemoveManaged(writes);
            return Commit(location,appData,writes);
        }, () => snapshot = SteamCache.Capture(location.Root));
    }
}
