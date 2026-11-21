using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamUnify;

public enum DynamicCollectionStatus { Created, UpToDate }

public sealed record DynamicCollectionResult(DynamicCollectionStatus Status, string Name, string BackupPath)
{
    public bool Created => Status == DynamicCollectionStatus.Created;
}

public static partial class SteamLibrary
{
    internal static string DynamicOwnerPath(SteamLocation location, string data) =>
        Path.Combine(Canon(data), $"all-games-{RootKey(location.Root)}-{location.Account}.json");

    public static DynamicCollectionResult EnsureAllGamesCollection(SteamLocation location, string appData, bool restartSteam,
        Action? beforeClientShutdown = null)
    {
        if (new SteamCollections(location, appData).AllGamesCollectionName is string existing)
            return new(DynamicCollectionStatus.UpToDate, existing, "");
        return WithClosedClient(location, restartSteam, () =>
        {
            // Steam may have flushed newer collection data while closing.
            var collections = new SteamCollections(location, appData);
            if (collections.AllGamesCollectionName is string name)
                return new DynamicCollectionResult(DynamicCollectionStatus.UpToDate, name, "");
            var writes = new Dictionary<string,byte[]?>(Paths);
            string label = collections.PlanAllGames(writes);
            return new DynamicCollectionResult(DynamicCollectionStatus.Created, label, Commit(location, appData, writes));
        }, beforeClientShutdown);
    }
}

internal sealed partial class SteamCollections
{
    const string AllGames = "全部游戏";
    const string AlternateName = AllGames + " · 统译";
    readonly string dynamicOwnerPath;
    readonly string? dynamicOwner;
    JsonObject? ownedDynamicRecord;
    string? ownedDynamicName, matchingDynamicName;
    int matchingRank = int.MaxValue;
    internal string? AllGamesCollectionName => ownedDynamicName ?? matchingDynamicName;

    // Valve's v2 collection filter evaluates this status group against every visible library app.
    // Played (3) OR never played (4) includes both installed and uninstalled entries.
    static JsonObject CreateLibraryFilter()
    {
        var groups = new JsonArray();
        for (int i = 0; i < 9; i++)
            groups.Add(new JsonObject { ["rgOptions"] = i == 1 ? new JsonArray(3, 4) : new JsonArray(),
                ["bAcceptUnion"] = i == 0 || i == 1 });
        return new JsonObject { ["nFormatVersion"] = 2, ["strSearchText"] = "", ["filterGroups"] = groups,
            ["setSuggestions"] = new JsonObject() };
    }

    static bool IsAllGamesRule(JsonObject value)
    {
        if (value["filterSpec"] is not JsonObject spec ||
            !JsonNode.DeepEquals(spec["nFormatVersion"], JsonValue.Create(2)) ||
            !string.IsNullOrEmpty(spec["strSearchText"]?.ToString()) ||
            spec["filterGroups"] is not JsonArray { Count: 9 } groups ||
            value["added"] is not (null or JsonArray { Count: 0 }) ||
            value["removed"] is not (null or JsonArray { Count: 0 })) return false;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i] is not JsonObject group || group["rgOptions"] is not JsonArray options) return false;
            if (i != 1)
            {
                if (options.Count != 0) return false;
                continue;
            }
            if (!JsonNode.DeepEquals(group["bAcceptUnion"], JsonValue.Create(true)) || options.Count != 2 ||
                !options.Any(v => JsonNode.DeepEquals(v, JsonValue.Create(3))) ||
                !options.Any(v => JsonNode.DeepEquals(v, JsonValue.Create(4)))) return false;
        }
        return true;
    }

    static int NameRank(string name)
    {
        if (name == AllGames) return 0;
        if (name == AlternateName) return 1;
        return name.StartsWith(AlternateName + " ", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(AlternateName.Length + 1), out int n) && n >= 2 ? n : int.MaxValue;
    }

    void ReadAllGamesCandidate(string id, JsonObject record, JsonObject value)
    {
        string name = value["name"]?.ToString() ?? "";
        int rank = NameRank(name);
        if (name.Length == 0 || (id != dynamicOwner && rank >= matchingRank) || !IsAllGamesRule(value)) return;
        if (id == dynamicOwner)
        {
            ownedDynamicRecord = record;
            ownedDynamicName = name;
        }
        if (rank < matchingRank)
        {
            matchingRank = rank;
            matchingDynamicName = name;
        }
    }

    internal string PlanAllGames(Dictionary<string,byte[]?> writes)
    {
        var versions = ReadVersions(out var nsVersion);
        var name = AllGames;
        for (int n = 1; existingNames.Contains(name); n++)
            name = n == 1 ? AlternateName : $"{AlternateName} {n}";
        var id = "uc-" + Guid.NewGuid().ToString("N")[..16];
        var key = Prefix + id;
        var value = new JsonObject { ["id"] = id, ["name"] = name, ["added"] = new JsonArray(),
            ["removed"] = new JsonArray(), ["filterSpec"] = CreateLibraryFilter() };
        var record = new JsonObject { ["key"] = key, ["value"] = value.ToJsonString(), ["timestamp"] = now,
            ["version"] = (++version).ToString(), ["conflictResolutionMethod"] = "custom", ["strMethodId"] = "union-collections" };
        records.Add(new JsonArray(JsonValue.Create(key), record));
        WriteVersions(versions, nsVersion, writes);
        writes[dynamicOwnerPath] = JsonSerializer.SerializeToUtf8Bytes(id);
        return name;
    }
}
