using System.Security.Cryptography;
using SteamUnify;

internal static class RecommendedChecks
{
    internal static void Run(SteamLocation loc,string data,string cacheFile)
    {
        void Check(bool ok,string reason) { if (!ok) throw new Exception("recommended batch: "+reason); }
        List<RecommendedEdit> ReadPlan()
        {
            var memberships=SteamLibrary.Memberships(loc,data);
            return SteamCache.Read(loc.Root,[]).Where(g=>g.AppId is 1100001 or 1100002).Select(g=>
                new RecommendedEdit(new(g.AppId,g.Localized["schinese"],"推荐收藏"),g.Name,memberships.GetValueOrDefault(g.AppId)??[])).ToList();
        }
        var shown=ReadPlan();
        Check(shown.Count==2,"fixture has two shown suggestions");
        var originalCache=File.ReadAllBytes(cacheFile);
        void Closed() => throw new Exception("closed fixture requested shutdown");
        Dictionary<string,string> Contents() => new[]{loc.Root,data}.SelectMany(dir=>Directory.GetFiles(dir,"*",SearchOption.AllDirectories))
            .ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        void ExpectStale(IReadOnlyList<RecommendedEdit> displayed,bool autoAccount=false)
        {
            var before=Contents();
            bool stale=false;
            try { SteamLibrary.ApplyRecommended(loc,data,false,displayed,autoAccount,Closed,ReadPlan); }
            catch(StaleRecommendationException) { stale=true; }
            var after=Contents();
            Check(stale && before.Count==after.Count && before.All(p=>after.GetValueOrDefault(p.Key)==p.Value),"stale batch did not write or back up");
        }
        var first=SteamCache.Read(loc.Root,[]).Single(g=>g.AppId==1100001);
        void ChangeCache(NameChange change)
        {
            foreach(var (file,bytes) in SteamCache.PlanNames(loc.Root,new Dictionary<int,NameChange>{{first.AppId,change}}))
                File.WriteAllBytes(file,bytes);
        }
        ChangeCache(new(first.Name,new(first.Localized){["schinese"]="Steam 后来更新的译名"}));
        ExpectStale(shown); // Same displayed English name and memberships, only the trusted recommendation changed.
        ChangeCache(new("Edited in Steam",first.Localized));
        ExpectStale(shown);
        File.WriteAllBytes(cacheFile,originalCache);
        var moved=shown.Select(r=>r with {CurrentGroups=["旧收藏"]}).ToList();
        ExpectStale(moved);
        ExpectStale(shown.Take(1).ToList());
        ExpectStale(shown.Select(r=>r with {Edit=r.Edit with {Collection="旧建议"}}).ToList());

        string login=Path.Combine(loc.Root,"config","loginusers.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(login)!);
        Directory.CreateDirectory(Path.Combine(loc.Root,"userdata","83","config"));
        const string switchedLogin="\"users\" { \"76561197960265811\" { \"MostRecent\" \"1\" } }";
        File.WriteAllText(login,switchedLogin);
        Check(SteamLibrary.Detect(loc.Root,null).Account=="83","most recent account belongs to the selected root");
        ExpectStale(shown,true);
        File.WriteAllText(login,"\"users\" { \"76561197960265770\" { \"MostRecent\" \"1\" } }");
        bool switchedDuringRefresh=false;
        try
        {
            SteamLibrary.ApplyRecommended(loc,data,false,shown,true,Closed,()=>{
                var fresh=ReadPlan();
                File.WriteAllText(login,switchedLogin);
                return fresh;
            });
        }
        catch(StaleRecommendationException) { switchedDuringRefresh=true; }
        Check(switchedDuringRefresh && !SteamLibrary.Backups(loc,data).Any() &&
            File.ReadAllBytes(cacheFile).SequenceEqual(originalCache),"account switch during read did not commit");
        var phases=new System.Collections.Concurrent.ConcurrentQueue<string>();
        string backup=SteamLibrary.ApplyRecommended(loc,data,false,shown,false,Closed,()=>ReadPlan().AsEnumerable().Reverse().ToList(),
            (stage,_,_)=>phases.Enqueue(stage));
        Check(phases.First()=="核对游戏库" && phases.Contains("准备更改") && phases.Last()=="写入 Steam","recommended validation and committing progress");
        Check(Directory.Exists(backup) && SteamLibrary.Memberships(loc,data)[first.AppId].Single()=="推荐收藏" &&
            SteamCache.Read(loc.Root,[]).Single(g=>g.AppId==first.AppId).Name==first.Localized["schinese"],"stable pinned account writes full batch by ID");
        SteamLibrary.Restore(loc,backup,false,data);
        Check(File.ReadAllBytes(cacheFile).SequenceEqual(originalCache) && SteamLibrary.Memberships(loc,data).Count==0,"restores recommendation backup");
        Console.WriteLine("PASS atomic recommended batch, changed official translation, name, group, scope, account, cancellation, and restore");
    }
}
