using System.Diagnostics;
using System.Text.Json.Nodes;
using SteamUnify;

internal static class DynamicCollectionChecks
{
    internal static void Run(string root, string data)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("dynamic collection: " + message);
        }
        string Cloud(string account)
        {
            string dir = Path.Combine(root, "userdata", account, "config", "cloudstorage");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "cloud-storage-namespace-1.json");
        }
        JsonArray Record(string id, string name, JsonObject? filter = null)
        {
            string key = "user-collections." + id;
            var value = new JsonObject { ["id"] = id, ["name"] = name, ["added"] = new JsonArray(), ["removed"] = new JsonArray() };
            if (filter != null) value["filterSpec"] = filter.DeepClone();
            return new JsonArray(JsonValue.Create(key), new JsonObject { ["key"] = key, ["version"] = "45", ["value"] = value.ToJsonString() });
        }
        JsonArray ReadRows(string file) => JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        JsonObject Value(JsonNode? row) => JsonNode.Parse(row![1]!["value"]!.GetValue<string>())!.AsObject();
        var loc = new SteamLocation(root, "84");
        var peer = new SteamLocation(root, "85");
        string path = Cloud(loc.Account), other = Cloud(peer.Account);
        string versions = Path.Combine(Path.GetDirectoryName(path)!, "cloud-storage-namespaces.json");
        var start = new JsonArray(Record("favorite", "我的最爱"), Record("uc-user", "全部游戏"), Record("uc-other", "全部游戏 · 统译"));
        File.WriteAllText(path, start.ToJsonString());
        File.WriteAllText(versions, "[[1,\"70\"]]");

        var created = SteamLibrary.EnsureAllGamesCollection(loc, data, false, () => throw new Exception("closed client prompted for shutdown"));
        Check(created.Created && created.Name == "全部游戏 · 统译 2" && Directory.Exists(created.BackupPath), "name collisions and backup");
        var rows = ReadRows(path);
        for (int i = 0; i < start.Count; i++) Check(JsonNode.DeepEquals(start[i], rows[i]), "preserves existing collection");
        var record = rows[3]![1]!;
        var v = Value(rows[3]);
        Check(record["version"]!.ToString() == "71" && JsonNode.Parse(File.ReadAllText(versions))![0]![1]!.ToString() == "71", "namespace version");
        Check(v["added"]!.AsArray().Count == 0 && v["removed"]!.AsArray().Count == 0, "no explicit app list");
        var filter = v["filterSpec"]!.AsObject();
        var groups = filter["filterGroups"]!.AsArray();
        Check(filter["nFormatVersion"]!.GetValue<int>() == 2 && filter["strSearchText"]!.ToString() == "" && groups.Count == 9, "Valve filter format");
        Check(groups[1]!["bAcceptUnion"]!.GetValue<bool>() && groups[1]!["rgOptions"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual([3,4]) &&
            groups.Where((g,i) => i != 1).All(g => g!["rgOptions"]!.AsArray().Count == 0), "played OR never played, no installation or other restriction");
        Check(SteamLibrary.Memberships(loc, data).Count == 0, "dynamic set excluded from manual classification");
        string before = File.ReadAllText(path);
        var repeat = SteamLibrary.EnsureAllGamesCollection(loc, data, false);
        Check(!repeat.Created && repeat.Name == created.Name && repeat.BackupPath == "" && File.ReadAllText(path) == before &&
            SteamLibrary.Backups(loc, data).Count == 1, "repeat is read-only");

        v["name"] = "我的总览";
        record["value"] = v.ToJsonString();
        File.WriteAllText(path, rows.ToJsonString());
        Check(new SteamCollections(loc, data).AllGamesCollectionName == "我的总览" &&
            SteamLibrary.EnsureAllGamesCollection(loc, data, false).Name == "我的总览", "recognizes renamed owned rule");
        string undo = SteamLibrary.UndoManaged(loc, data, false);
        rows = ReadRows(path);
        Check(rows[3]![1]!["is_deleted"]!.GetValue<bool>() && !rows[3]![1]!.AsObject().ContainsKey("value") &&
            new SteamCollections(loc, data).AllGamesCollectionName == null, "undo targeted tombstone and ownership");
        for (int i = 0; i < start.Count; i++) Check(JsonNode.DeepEquals(start[i], rows[i]), "undo keeps personal set");
        Check(SteamLibrary.UndoManaged(loc, data, false) == "", "repeat undo unchanged");
        SteamLibrary.Restore(loc, undo, false, data);
        Check(new SteamCollections(loc, data).AllGamesCollectionName == "我的总览", "restore undo reinstates ownership");
        SteamLibrary.Restore(loc, created.BackupPath, false, data);
        Check(File.ReadAllText(path) == start.ToJsonString() && File.ReadAllText(versions) == "[[1,\"70\"]]" &&
            !File.Exists(SteamLibrary.DynamicOwnerPath(loc, data)), "byte-exact pre-creation restore");

        var personal = new JsonArray(Record("uc-personal", "我的实验", filter));
        File.WriteAllText(other, personal.ToJsonString());
        var otherCreated = SteamLibrary.EnsureAllGamesCollection(peer, data, false);
        Check(otherCreated.Created && otherCreated.Name == "全部游戏" && File.ReadAllText(path) == start.ToJsonString(), "separate accounts and unrelated names");
        rows = ReadRows(other);
        var edited = Value(rows[1]);
        edited["removed"]!.AsArray().Add(123);
        rows[1]![1]!["value"] = edited.ToJsonString();
        File.WriteAllText(other, rows.ToJsonString());
        var replacement = SteamLibrary.EnsureAllGamesCollection(peer, data, false);
        Check(replacement.Created && replacement.Name == "全部游戏 · 统译", "respects manual membership exclusion");
        SteamLibrary.UndoManaged(peer, data, false);
        var afterUndo = ReadRows(other);
        Check(JsonNode.DeepEquals(afterUndo[0], rows[0]) && JsonNode.DeepEquals(afterUndo[1], rows[1]), "undo leaves customized prior collection intact");

        var existing = new JsonArray(Record("uc-home", "全部游戏", filter));
        var reversed = Value(existing[0]);
        reversed["filterSpec"]!["filterGroups"]![1]!["rgOptions"] = new JsonArray(4, 3);
        existing[0]![1]!["value"] = reversed.ToJsonString();
        File.WriteAllText(other, existing.ToJsonString());
        int backups = SteamLibrary.Backups(peer, data).Count;
        var adopted = SteamLibrary.EnsureAllGamesCollection(peer, data, false);
        Check(!adopted.Created && adopted.Name == "全部游戏" && adopted.BackupPath == "" &&
            SteamLibrary.UndoManaged(peer, data, false) == "" && File.ReadAllText(other) == existing.ToJsonString() &&
            SteamLibrary.Backups(peer, data).Count == backups, "equivalent personal rule reused without claiming ownership");
        // This is a command interpreter waiting on its stdin, living entirely inside the temporary fixture.
        string dummy = Path.Combine(root, "steam.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), dummy);
        using (var fake = Process.Start(new ProcessStartInfo(dummy, "/d /q /k") { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            try
            {
                var unchanged = SteamLibrary.EnsureAllGamesCollection(peer, data, false, () => throw new Exception("unchanged library prompted"));
                Check(!unchanged.Created && !fake.HasExited, "existing rule avoids shutdown consent");
                int beforeCancel = SteamLibrary.Backups(loc, data).Count;
                bool cancelled = false;
                try { SteamLibrary.EnsureAllGamesCollection(loc, data, true, () => throw new OperationCanceledException()); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !fake.HasExited && File.ReadAllText(path) == start.ToJsonString() &&
                    File.ReadAllText(versions) == "[[1,\"70\"]]" && !File.Exists(SteamLibrary.DynamicOwnerPath(loc, data)) &&
                    SteamLibrary.Backups(loc, data).Count == beforeCancel, "cancellation preserves running client, collection, versions, and backup count");
                bool recommendationCancelled = false;
                try
                {
                    SteamLibrary.ApplyRecommended(loc,data,true,[new(new(1100001,"新的推荐",null),"旧名称",[])],false,
                        ()=>throw new OperationCanceledException(),()=>throw new Exception("cancelled recommendation started planning"));
                }
                catch (OperationCanceledException) { recommendationCancelled = true; }
                Check(recommendationCancelled && !fake.HasExited && File.ReadAllText(path)==start.ToJsonString() &&
                    SteamLibrary.Backups(loc,data).Count==beforeCancel, "recommended batch cancellation preserves client and files");
            }
            finally
            {
                if (!fake.HasExited) fake.StandardInput.WriteLine("exit");
                fake.StandardInput.Close();
                if (!fake.WaitForExit(2000)) { fake.Kill(); fake.WaitForExit(); }
            }
        }
        Console.WriteLine("PASS Steam v2 dynamic library collection, idempotence, personal rules, name conflicts, account isolation, undo and exact restore");
    }
}
