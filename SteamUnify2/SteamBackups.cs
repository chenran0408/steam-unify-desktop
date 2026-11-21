using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUnify;

public static partial class SteamLibrary
{
    private record SavedFile(string Target, string? Sha256, FileAttributes Attributes);
    private record Snapshot(int Version, string SteamRoot, string Account, List<SavedFile> Files);
    private const string Manifest = "snapshot.json";
    private const string OwnerToken = "@collections";
    private const string DynamicOwnerToken = "@all-games";
    private const string OriginalsToken = "@originals";
    private static string BackupRoot(SteamLocation loc, string data) =>
        Path.Combine(Canon(data),"backups",RootKey(loc.Root) + "-" + loc.Account);
    private static string Original(string path, int index) => Path.Combine(path, $"file-{index:D5}.bin");

    public static List<string> Backups(SteamLocation location, string appData)
    {
        string root = BackupRoot(location,appData);
        var list = Directory.Exists(root) ? Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && File.Exists(Path.Combine(d,Manifest))).ToList() : new();
        string? parent = Path.GetDirectoryName(Canon(appData));
        var oldRoots = new HashSet<string>(Paths) { Path.Combine(appData,"backups") };
        if (parent != null) oldRoots.Add(Path.Combine(parent,"backups"));
        foreach (string basePath in oldRoots.Where(Directory.Exists))
        {
            foreach (string folder in Directory.EnumerateDirectories(basePath))
            {
                string file = Path.Combine(folder,"manifest.json");
                if (!File.Exists(file)) continue;
                using var legacy = JsonDocument.Parse(File.ReadAllBytes(file));
                if (MatchesLegacy(legacy.RootElement, location)) list.Add(folder);
            }
        }
        return list.OrderByDescending(Directory.GetCreationTimeUtc).ThenByDescending(d => d,StringComparer.Ordinal).ToList();
    }

    static bool MatchesLegacy(JsonElement json, SteamLocation loc) =>
        json.TryGetProperty("account",out var account) && account.ToString() == loc.Account &&
        json.TryGetProperty("steam_root",out var root) && root.ValueKind == JsonValueKind.String &&
        Path.IsPathFullyQualified(root.GetString()!) && Paths.Equals(Canon(root.GetString()!),Canon(loc.Root));

    public static void Restore(SteamLocation location, string backupPath, bool restartSteam, string? appData = null)
    {
        string folder = Canon(backupPath);
        bool native = File.Exists(Path.Combine(folder,Manifest));
        string data = appData ?? (native
            ? Directory.GetParent(folder)?.Parent?.Parent?.FullName
            : Path.Combine(Directory.GetParent(folder)?.Parent?.FullName ?? folder,"data"))
            ?? throw new InvalidDataException("备份目录无效。");
        WithClosedClient(location,restartSteam,() =>
        {
            var writes = new Dictionary<string,byte[]?>(Paths);
            var attributes = new Dictionary<string,FileAttributes>(Paths);
            if (native)
            {
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(Path.Combine(folder,Manifest)))
                    ?? throw new InvalidDataException("备份说明文件为空。");
                if (snapshot.Version != 1 || !Paths.Equals(Canon(location.Root),Canon(snapshot.SteamRoot)) || location.Account != snapshot.Account)
                    throw new InvalidDataException("此备份属于其他 Steam 目录、账号或版本。");
                var allowed = AllowedDestinations(location,data);
                for (int i = 0; i < snapshot.Files.Count; ++i)
                {
                    var entry = snapshot.Files[i];
                    string target = entry.Target switch
                    {
                        OwnerToken => OwnershipPath(location,data),
                        DynamicOwnerToken => DynamicOwnerPath(location,data),
                        OriginalsToken => OriginalsPath(location,data),
                        _ => Canon(entry.Target)
                    };
                    if (!allowed(target)) throw new InvalidDataException("备份引用了此账号以外的文件：" + target);
                    writes.Add(target, ReadOriginal(folder,i,entry));
                    attributes[target] = entry.Attributes;
                }
            }
            else LoadLegacy(location,folder,data,writes);
            return Commit(location,data,writes,attributes);
        });
    }

    private static Func<string,bool> AllowedDestinations(SteamLocation loc, string data)
    {
        string root = Canon(loc.Root);
        string cache = Path.Combine(root, "appcache");
        string shardDir = Path.Combine(cache,"appinfo");
        string cloud = Path.Combine(root,"userdata",loc.Account,"config","cloudstorage");
        var files = new HashSet<string>(Paths) { OwnershipPath(loc,data), DynamicOwnerPath(loc,data), OriginalsPath(loc,data), Path.Combine(cache,"appinfo.vdf"),
            Path.Combine(root,"steamapps","appinfo.vdf"), Path.Combine(cloud,"cloud-storage-namespace-1.json"),
            Path.Combine(cloud,"cloud-storage-namespaces.json") };
        var libs = Libraries(root).Select(p => Path.Combine(p,"steamapps")).ToHashSet(Paths);
        return path => files.Contains(path) || (Paths.Equals(Path.GetDirectoryName(path),shardDir) &&
            Regex.IsMatch(Path.GetFileName(path),@"^appinfo_[0-9a-f]+\.vdf$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
            || (libs.Contains(Path.GetDirectoryName(path)!) &&
            Regex.IsMatch(Path.GetFileName(path),@"^appmanifest_[0-9]+\.acf$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant));
    }

    static void LoadLegacy(SteamLocation loc, string folder, string appData, Dictionary<string,byte[]?> writes)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder,"manifest.json")));
        var manifest = doc.RootElement;
        if (!MatchesLegacy(manifest,loc)) throw new InvalidDataException("这份旧版备份属于其他 Steam 目录或账号。");
        var cloud = Path.Combine("userdata",loc.Account,"config","cloudstorage");
        foreach (string name in new[] {"cloud-storage-namespace-1.json","cloud-storage-namespaces.json"})
        {
            string original = Path.Combine(folder,cloud,name);
            if (File.Exists(original)) writes[Path.Combine(loc.Root,cloud,name)] = File.ReadAllBytes(original);
        }
        string shardDir = Path.Combine(loc.Root,"appcache","appinfo");
        string source = Path.Combine(folder,"appinfo");
        bool liveShards = Directory.Exists(shardDir) && Directory.EnumerateFiles(shardDir,"appinfo_*.vdf").Any();
        bool oldShards = Directory.Exists(source) && Directory.EnumerateFiles(source,"appinfo_*.vdf").Any();
        foreach (string relative in new[]{Path.Combine("appcache","appinfo.vdf"),Path.Combine("steamapps","appinfo.vdf")})
        {
            string old = Path.Combine(folder,relative);
            if (!File.Exists(old)) continue;
            if (liveShards && !oldShards) throw new InvalidDataException("Steam 缓存已切换为分片格式，这份旧版单文件备份无法覆盖当前格式。");
            writes[Path.Combine(loc.Root,relative)] = File.ReadAllBytes(old);
        }
        if (oldShards)
        {
            if (!liveShards && File.Exists(Path.Combine(loc.Root,"appcache","appinfo.vdf")))
                throw new InvalidDataException("此版本的 Steam 缓存与分片备份格式不同。");
            foreach (string old in Directory.EnumerateFiles(source,"appinfo_*.vdf"))
                writes[Path.Combine(shardDir,Path.GetFileName(old))] = File.ReadAllBytes(old);
        }
        string manifests = Path.Combine(folder,"acf");
        if (Directory.Exists(manifests))
            foreach ((int id,string path) in FindInstalled(loc.Root))
            {
                string old = Path.Combine(manifests,$"appmanifest_{id}.acf");
                if (File.Exists(old)) writes[path] = File.ReadAllBytes(old);
            }
        if (manifest.TryGetProperty("managed_collections",out var managed))
        {
            var mapping = managed.Deserialize<Dictionary<string,string>>() ?? new();
            writes[OwnershipPath(loc,appData)] = JsonSerializer.SerializeToUtf8Bytes(LegacyOwners(loc,appData,mapping));
        }
        if (writes.Count == 0) throw new InvalidDataException("备份中没有可还原的文件。");
    }

    static byte[]? ReadOriginal(string folder, int index, SavedFile file)
    {
        if (file.Sha256 == null) return null;
        var original = File.ReadAllBytes(Original(folder,index));
        if (Convert.ToHexString(SHA256.HashData(original)) != file.Sha256)
            throw new InvalidDataException($"备份校验失败：{file.Target}");
        return original;
    }

    static string Commit(SteamLocation loc, string data, Dictionary<string,byte[]?> writes,
        Dictionary<string,FileAttributes>? desiredAttributes = null,Action<string,int,int>? progress=null)
    {
        if (writes.Count == 0) return "";
        string basePath = BackupRoot(loc,data);
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string final = Path.Combine(basePath,id);
        string pending = Path.Combine(basePath,".pending-" + id);
        string ownership = OwnershipPath(loc,data);
        string dynamicOwner = DynamicOwnerPath(loc,data);
        string originals = OriginalsPath(loc,data);
        Directory.CreateDirectory(pending);
        var entries = new List<SavedFile>();
        var operations = new List<(string Target,string? Stage,FileAttributes Attributes)>();
        var targets = writes.ToArray();
        var prepared = new (SavedFile Entry,FileAttributes Attributes)?[targets.Length];
        var stagePaths = new string?[targets.Length];
        var staged = new List<string>();
        bool published = false;
        try
        {
            void Prepare(int index)
            {
                var (target,bytes) = targets[index];
                bool exists = File.Exists(target);
                if (!exists && bytes == null) return;
                string? hash = null;
                FileAttributes attr = exists ? File.GetAttributes(target) : FileAttributes.Normal;
                FileAttributes newAttr = desiredAttributes?.GetValueOrDefault(target,attr) ?? attr;
                if (exists)
                {
                    using var input = File.OpenRead(target);
                    hash = Convert.ToHexString(SHA256.HashData(input));
                    if (bytes != null && input.Length == bytes.Length && attr == newAttr &&
                        hash == Convert.ToHexString(SHA256.HashData(bytes))) return;
                    input.Position = 0;
                    using var copy = File.Create(Original(pending,index));
                    input.CopyTo(copy);
                    copy.Flush(true);
                }
                if (bytes != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var stage = target + ".steamunify-" + id + ".tmp";
                    using var output = new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    stagePaths[index] = stage;
                    output.Write(bytes);
                    output.Flush(true);
                }
                string token = Paths.Equals(target, ownership) ? OwnerToken :
                    Paths.Equals(target, dynamicOwner) ? DynamicOwnerToken :
                    Paths.Equals(target, originals) ? OriginalsToken : target;
                prepared[index] = (new SavedFile(token,hash,attr),newAttr);
            }
            // Each worker owns distinct snapshot/staging files. Steam destinations are replaced
            // sequentially below, after every original, replacement and manifest is durable.
            int finished=0;
            progress?.Invoke("备份文件",0,targets.Length);
            void PrepareWithProgress(int i)
            {
                Prepare(i);
                progress?.Invoke("备份文件",Interlocked.Increment(ref finished),targets.Length);
            }
            if (targets.Length < 8)
                for (int i=0; i<targets.Length; i++) PrepareWithProgress(i);
            else
            {
                var workers = new ParallelOptions { MaxDegreeOfParallelism=Math.Clamp(Environment.ProcessorCount*2,4,16) };
                try { Parallel.For(0,targets.Length,workers,PrepareWithProgress); }
                catch (AggregateException ex) when (ex.InnerExceptions.Count==1)
                { ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw(); }
            }
            for (int i=0; i<prepared.Length; i++)
            {
                if (prepared[i] is not { } op) continue;
                if (i!=entries.Count && op.Entry.Sha256!=null)
                    File.Move(Original(pending,i),Original(pending,entries.Count));
                entries.Add(op.Entry);
                operations.Add((targets[i].Key,stagePaths[i],op.Attributes));
            }
            if (entries.Count == 0) return "";
            var snapshot = new Snapshot(1,Canon(loc.Root),loc.Account,entries);
            using (var file = File.Create(Path.Combine(pending,Manifest)))
            {
                JsonSerializer.Serialize(file,snapshot);
                file.Flush(true);
            }
            Directory.Move(pending,final);
            published = true;
            int attempted = 0;
            try
            {
                progress?.Invoke("写入 Steam",0,operations.Count);
                foreach (var op in operations)
                {
                    attempted++;
                    Install(op.Target,op.Stage,op.Attributes);
                    progress?.Invoke("写入 Steam",attempted,operations.Count);
                }
            }
            catch (Exception error)
            {
                var failures = new List<Exception>{error};
                for (int i = attempted - 1; i >= 0; i--)
                {
                    var op = operations[i];
                    try
                    {
                        var entry = entries[i];
                        if (entry.Sha256 != null && File.Exists(op.Target))
                        {
                            using var input = File.OpenRead(op.Target);
                            if (Convert.ToHexString(SHA256.HashData(input)) == entry.Sha256)
                            {
                                input.Dispose();
                                SetFlags(op.Target,entry.Attributes);
                                continue;
                            }
                        }
                        string? rollback = null;
                        if (entry.Sha256 != null)
                        {
                            rollback = op.Target + ".rollback-" + id;
                            File.Copy(Original(final,i),rollback);
                            staged.Add(rollback);
                        }
                        Install(op.Target,rollback,entry.Attributes);
                    }
                    catch (Exception rollbackError) { failures.Add(rollbackError); }
                }
                if (failures.Count > 1) throw new AggregateException($"写入和部分回滚失败，请从 {final} 还原。",failures);
                throw;
            }
            return final;
        }
        finally
        {
            foreach (string path in stagePaths.OfType<string>().Concat(staged)) if (File.Exists(path)) File.Delete(path);
            if (!published && Directory.Exists(pending)) Directory.Delete(pending,true);
        }
    }

    const FileAttributes EditableFlags = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
        FileAttributes.Archive | FileAttributes.NotContentIndexed;
    static void SetFlags(string path, FileAttributes flags)
    {
        var desired = flags & EditableFlags;
        if ((File.GetAttributes(path) & EditableFlags) != desired)
            File.SetAttributes(path,desired == 0 ? FileAttributes.Normal : desired);
    }

    static void Install(string target, string? stage, FileAttributes attrs)
    {
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(target,File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        if (stage == null) File.Delete(target);
        else
        {
            File.Move(stage,target,true);
            SetFlags(target,attrs);
        }
    }
}
