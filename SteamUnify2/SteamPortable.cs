using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SteamUnify;

public static partial class SteamLibrary
{
    private sealed record PortableIndex(int Version,string Root,string Account,List<PortablePoint> Points);
    private sealed record PortablePoint(string Key,string Label,bool Legacy,DateTime Created);
    private static readonly Regex ManifestFile = new(@"\Aappmanifest_([0-9]+)\.acf\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex ShardFile = new(@"\Aappinfo_[0-9a-f]+\.vdf\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);

    private static Dictionary<string,string> PortableData(SteamLocation location,string data)
    {
        var files = new[] {"desktop.json","settings.json","state.json","descriptions.json","suggestions.v2.json"}
            .ToDictionary(name=>name,name=>Path.Combine(data,name),Paths);
        files.Add("managed.json",OwnershipPath(location,data));
        files.Add("dynamic.json",DynamicOwnerPath(location,data));
        files.Add("originals.json",OriginalsPath(location,data));
        files.Add("seen.json",Path.Combine(data,"seen-"+Path.GetFileName(OwnershipPath(location,data))));
        return files;
    }

    private static bool LegacyBackupFile(string relative,string account)
    {
        if (relative is "manifest.json" or "appcache/appinfo.vdf" or "steamapps/appinfo.vdf") return true;
        if (relative.StartsWith("appinfo/",StringComparison.Ordinal) && ShardFile.IsMatch(relative[8..])) return true;
        if (relative.StartsWith("acf/",StringComparison.Ordinal) && ManifestFile.IsMatch(relative[4..])) return true;
        string config = $"userdata/{account}/config/";
        return relative == config+"localconfig.vdf" ||
            relative == config+"cloudstorage/cloud-storage-namespace-1.json" ||
            relative == config+"cloudstorage/cloud-storage-namespaces.json";
    }

    public static int ExportPortable(SteamLocation location,string data,string path)
    {
        var backups = Backups(location,data);
        var points = new List<PortablePoint>();
        string stage = path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using (var archive = ZipFile.Open(stage,ZipArchiveMode.Create))
            {
                foreach (var (key,filename) in PortableData(location,data))
                    if (File.Exists(filename)) archive.CreateEntryFromFile(filename,"data/"+key);
                foreach (string folder in backups)
                {
                    string key = "b"+points.Count.ToString("D5");
                    bool legacy = !File.Exists(Path.Combine(folder,Manifest));
                    if (legacy)
                    {
                        foreach (string file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
                        {
                            string relative = Path.GetRelativePath(folder,file).Replace('\\','/');
                            if (LegacyBackupFile(relative,location.Account))
                                archive.CreateEntryFromFile(file,$"snapshots/{key}/{relative}");
                        }
                    }
                    else
                    {
                        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(Path.Combine(folder,Manifest)))!;
                        archive.CreateEntryFromFile(Path.Combine(folder,Manifest),$"snapshots/{key}/{Manifest}");
                        for (int i=0;i<snapshot.Files.Count;i++)
                            if (snapshot.Files[i].Sha256!=null)
                                archive.CreateEntryFromFile(Original(folder,i),$"snapshots/{key}/file-{i:D5}.bin");
                    }
                    points.Add(new(key,Path.GetFileName(folder),legacy,Directory.GetCreationTimeUtc(folder)));
                }
                using var manifest = archive.CreateEntry("portable.json").Open();
                JsonSerializer.Serialize(manifest,new PortableIndex(1,Canon(location.Root),location.Account,points));
            }
            File.Move(stage,path,true);
            return points.Count;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static T ReadZip<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("迁移包中有空白记录。");
    }

    private static Dictionary<string,ZipArchiveEntry> PortableEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName,entry))
                throw new InvalidDataException("迁移包中含有重复文件。");
        return entries;
    }

    private static ZipArchiveEntry Need(Dictionary<string,ZipArchiveEntry> entries,string name) =>
        entries.GetValueOrDefault(name) ?? throw new InvalidDataException("迁移包中缺少 "+name+"。");

    private static string? PortableTarget(string target,PortableIndex index,SteamLocation location,
        IReadOnlyDictionary<int,string> installed,Func<string,bool> allowed)
    {
        if (target is OwnerToken or DynamicOwnerToken or OriginalsToken) return target;
        if (!Path.IsPathFullyQualified(target)) throw new InvalidDataException("迁移包中的备份路径无效。");
        var sourcePath = Canon(target);
        if (ManifestFile.Match(Path.GetFileName(sourcePath)) is { Success:true } match &&
            int.TryParse(match.Groups[1].Value,out int appId))
        {
            if(!installed.TryGetValue(appId,out var mapped))return null;
            if(allowed(mapped))return mapped;
        }
        string relative = Path.GetRelativePath(index.Root,sourcePath);
        if (!Path.IsPathRooted(relative) && !relative.Split(Path.DirectorySeparatorChar).Contains(".."))
        {
            string destination = Canon(Path.Combine(location.Root,relative));
            if(allowed(destination))return destination;
        }
        throw new InvalidDataException("迁移包的备份包含未知的 Steam 文件。");
    }

    private static void WriteZipEntry(ZipArchiveEntry item,string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        item.ExtractToFile(destination);
    }

    private static bool StagePortablePoint(PortableIndex index,PortablePoint point,SteamLocation location,string data,
        Dictionary<string,ZipArchiveEntry> entries,string destination,IReadOnlyDictionary<int,string> installed)
    {
        string prefix = $"snapshots/{point.Key}/";
        Directory.CreateDirectory(destination);
        if (point.Legacy)
        {
            var manifest = ReadZip<JsonObject>(Need(entries,prefix+"manifest.json"));
            if (manifest["account"]?.ToString()!=index.Account || !Paths.Equals(manifest["steam_root"]?.ToString(),index.Root))
                throw new InvalidDataException("旧版快照与迁移包的 Steam 账号不一致。");
            foreach (var pair in entries.Where(pair=>pair.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))
            {
                var relative = pair.Key[prefix.Length..];
                if (!LegacyBackupFile(relative,index.Account)) throw new InvalidDataException("旧版快照含有未知文件。");
                if (relative!="manifest.json") WriteZipEntry(pair.Value,Path.Combine(destination,relative));
            }
            manifest["steam_root"]=location.Root;
            File.WriteAllText(Path.Combine(destination,"manifest.json"),manifest.ToJsonString());
            return true;
        }
        var snapshot = ReadZip<Snapshot>(Need(entries,prefix+Manifest));
        if(snapshot.Version!=1 || snapshot.Account!=index.Account || !Paths.Equals(snapshot.SteamRoot,index.Root))
            throw new InvalidDataException("快照与迁移包的 Steam 账号不一致。");
        var allowed = AllowedDestinations(location,data);
        var files = new List<SavedFile>();
        var targets = new HashSet<string>(Paths);
        for (int i=0;i<snapshot.Files.Count;i++)
        {
            var entry = snapshot.Files[i];
            string? target = PortableTarget(entry.Target,index,location,installed,allowed);
            if (target==null) continue;
            if(!targets.Add(target))throw new InvalidDataException("快照重复引用同一个 Steam 文件。");
            if (entry.Sha256!=null)
                WriteZipEntry(Need(entries,$"{prefix}file-{i:D5}.bin"),Original(destination,files.Count));
            files.Add(entry with {Target=target});
            if (entry.Sha256!=null) ReadOriginal(destination,files.Count-1,files[^1]);
        }
        if (files.Count==0) return false;
        File.WriteAllBytes(Path.Combine(destination,Manifest),JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1,location.Root,location.Account,files)));
        return true;
    }

    public static int ImportPortable(SteamLocation location,string data,string path,out string rollbackZip,
        Action<string>? validateLocalData=null)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = PortableEntries(archive);
        var index = ReadZip<PortableIndex>(Need(entries,"portable.json"));
        if(index.Version!=1 || !Path.IsPathFullyQualified(index.Root))throw new InvalidDataException("不支持此迁移包格式。");
        if(index.Account!=location.Account)throw new InvalidDataException("迁移包属于另一个 Steam 账号，请切换账号后再导入。");
        string work = Path.Combine(data,".portable-"+Guid.NewGuid().ToString("N"));
        var staged = new List<(string To,string From)>();
        var installed = FindInstalled(location.Root);
        var placed = new List<(string Target,byte[]? Before)>();
        var created = new List<string>();
        Directory.CreateDirectory(work);
        try
        {
            foreach(var (key,target) in PortableData(location,data))
            {
                if (!entries.TryGetValue("data/"+key,out var item)) continue;
                using var stream = item.Open();
                var json = JsonNode.Parse(stream) ?? throw new InvalidDataException("迁移包的本地记录为空。");
                if (key is "desktop.json" or "settings.json")
                {
                    string rootKey=key=="desktop.json"?"Root":"steam_root";
                    string accountKey=key=="desktop.json"?"Account":"account";
                    if (json[rootKey]?.ToString() is { Length:>0 }) json[rootKey]=location.Root;
                    if (json[accountKey]?.ToString() is { Length:>0 }) json[accountKey]=location.Account;
                }
                if (key=="seen.json") _ = json.Deserialize<HashSet<int>>() ?? throw new InvalidDataException("已扫描记录无效。");
                if (key=="managed.json") _ = json.Deserialize<Dictionary<string,string>>() ?? throw new InvalidDataException("收藏集归属无效。");
                if (key=="originals.json") _ = json.Deserialize<Dictionary<int,OriginalName>>() ?? throw new InvalidDataException("原名记录无效。");
                if (key=="dynamic.json") _ = json.GetValue<string>();
                string from = Path.Combine(work,key);
                File.WriteAllText(from,json.ToJsonString());
                staged.Add((target,from));
            }
            validateLocalData?.Invoke(work);
            string snapshotWork = Path.Combine(work,"snapshots");
            var points = new List<(PortablePoint Point,string Staged)>();
            foreach(var point in index.Points)
            {
                if(!Regex.IsMatch(point.Key,@"\Ab[0-9]{5}\z"))throw new InvalidDataException("迁移包的快照编号无效。");
                if(string.IsNullOrWhiteSpace(point.Label) || point.Label.IndexOfAny(Path.GetInvalidFileNameChars())>=0 ||
                    point.Label.StartsWith('.') || point.Label.EndsWith('.') || point.Label.EndsWith(' '))
                    throw new InvalidDataException("迁移包的快照名称无效。");
                string folder = Path.Combine(snapshotWork,point.Key);
                if(Directory.Exists(folder))throw new InvalidDataException("迁移包重复引用同一快照。");
                if(StagePortablePoint(index,point,location,data,entries,folder,installed))points.Add((point,folder));
            }

            string history = Path.Combine(data,"backups","portable");
            Directory.CreateDirectory(history);
            rollbackZip = Path.Combine(history,"导入前-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..8]+".zip");
            ExportPortable(location,data,rollbackZip);
            try
            {
                foreach (var (target,from) in staged)
                {
                    placed.Add((target,File.Exists(target)?File.ReadAllBytes(target):null));
                    File.Move(from,target,true);
                }
                foreach(var (point,from) in points)
                {
                    string parent = point.Legacy?Path.Combine(data,"backups"):BackupRoot(location,data);
                    Directory.CreateDirectory(parent);
                    string target = Path.Combine(parent,point.Label);
                    if(Directory.Exists(target))target += "-"+Guid.NewGuid().ToString("N")[..8];
                    Directory.Move(from,target);
                    created.Add(target);
                    Directory.SetCreationTimeUtc(target,point.Created);
                }
            }
            catch
            {
                foreach(string folder in created)Directory.Delete(folder,true);
                foreach(var (target,old) in placed)
                {
                    if(old==null) File.Delete(target);
                    else
                    {
                        string rollback = Path.Combine(work,"undo-"+Guid.NewGuid().ToString("N")+".tmp");
                        File.WriteAllBytes(rollback,old);
                        File.Move(rollback,target,true);
                    }
                }
                throw;
            }
            return points.Count;
        }
        finally { Directory.Delete(work,true); }
    }
}
