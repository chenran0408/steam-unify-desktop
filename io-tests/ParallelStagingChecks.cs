using System.Security.Cryptography;
using System.Collections.Concurrent;
using SteamUnify;

internal static class ParallelStagingChecks
{
    internal static void Run(string fixture)
    {
        const int count=24;
        string root=Path.Combine(fixture,"stageSteam"),data=Path.Combine(fixture,"stageData");
        Directory.CreateDirectory(Path.Combine(root,"appcache"));
        Directory.CreateDirectory(Path.Combine(root,"userdata","1","config","cloudstorage"));
        Directory.CreateDirectory(Path.Combine(root,"steamapps"));
        var loc=new SteamLocation(root,"1");
        string cache=Path.Combine(root,"appcache","appinfo.vdf");
        File.WriteAllBytes(cache,WriteTimings.MakeAppInfo(count,32));
        string Acf(int i) => Path.Combine(root,"steamapps",$"appmanifest_{WriteTimings.FirstId+i}.acf");
        string Initial(int i) => $"\"AppState\" {{ \"appid\" \"{WriteTimings.FirstId+i}\" \"name\" \"Original {i}\" }}";
        for(int i=0;i<count;i++)File.WriteAllText(Acf(i),Initial(i));
        var edits=Enumerable.Range(0,count).Select(i=>new LibraryEdit(WriteTimings.FirstId+i,"译名 "+i,null)).ToList();
        SortedDictionary<string,string> Snapshot() => new(Directory.GetFiles(root,"*",SearchOption.AllDirectories)
            .ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        void Check(bool ok,string name){if(!ok)throw new Exception("parallel staging: "+name);}
        var initial=Snapshot();
        var progress=new ConcurrentQueue<(string Stage,int Done,int Total)>();
        string backup=SteamLibrary.Apply(loc,edits,data,false,(s,d,t)=>progress.Enqueue((s,d,t)));
        var events=progress.ToArray();
        foreach(string stage in new[]{"备份文件","写入 Steam"})
        {
            var updates=events.Where(e=>e.Stage==stage).ToList();
            int total=updates[0].Total;
            Check(total>=count && updates.All(e=>e.Total==total) && updates.Select(e=>e.Done).Order().SequenceEqual(Enumerable.Range(0,total+1)),
                "real progress per prepared / installed file");
        }
        Check(Array.FindLastIndex(events,e=>e.Stage=="备份文件")<Array.FindIndex(events,e=>e.Stage=="写入 Steam"),"progress phase order");
        Check(Directory.Exists(backup) && File.ReadAllText(Acf(17)).Contains("译名 17"),"writes and backup");
        for(int i=0;i<count;i+=4)File.WriteAllText(Acf(i),Initial(i)); // identical files should not take archive slots
        File.Delete(Acf(5)); // the compensating backup must remember that this file did not exist
        var mixed=Snapshot();
        using(var locked=new FileStream(Acf(22),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            int before=SteamLibrary.Backups(loc,data).Count;
            bool rejected=false;
            try{SteamLibrary.Restore(loc,backup,false,data);}catch(IOException){rejected=true;}
            Check(rejected && SteamLibrary.Backups(loc,data).Count==before,"unreadable target prevents publication");
        }
        Check(Snapshot().SequenceEqual(mixed) && !Directory.EnumerateDirectories(data,".pending-*",SearchOption.AllDirectories).Any(),
            "failed staging cleans temporary files and preserves every target");
        using(var locked=new FileStream(Acf(22),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool rejected=false;
            try{SteamLibrary.Restore(loc,backup,false,data);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){rejected=true;}
            Check(rejected,"locked installation rolls back");
        }
        Check(Snapshot().SequenceEqual(mixed),"rollback after preparation preserves mixed current state");
        var beforeRestore=SteamLibrary.Backups(loc,data).ToHashSet();
        SteamLibrary.Restore(loc,backup,false,data);
        Check(Snapshot().SequenceEqual(initial),"mixed restore with compact snapshot slots");
        string reverse=SteamLibrary.Backups(loc,data).Single(p=>!beforeRestore.Contains(p));
        SteamLibrary.Restore(loc,reverse,false,data);
        Check(Snapshot().SequenceEqual(mixed),"compensating backup restores surviving and absent files byte-for-byte");
        Console.WriteLine("PASS parallel staging failures, serial replacement rollback, mixed-state backups and byte-exact restore");
    }
}
