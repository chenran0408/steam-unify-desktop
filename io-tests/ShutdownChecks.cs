using System.Diagnostics;
using SteamUnify;

internal static class ShutdownChecks
{
    internal static void Run(string test,string fixtureCache)
    {
        var root=Path.Combine(test,"logout");
        string data=Path.Combine(test,"logout-data");
        var location=new SteamLocation(root,"42");
        var cache=Path.Combine(root,"appcache");
        string originalLeaf=Path.GetFileName(fixtureCache);
        if(originalLeaf.StartsWith("appinfo_",StringComparison.Ordinal))cache=Path.Combine(cache,"appinfo");
        string info=Path.Combine(cache,originalLeaf);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(root,"userdata","42","config","cloudstorage"));
        File.Copy(fixtureCache,info);
        string exe=Path.Combine(root,"steam.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory,"ping.exe"),exe);
        void Check(bool ok,string message){if(!ok)throw new Exception("shutdown: "+message);}

        string RunOnExit(Func<Action<string,int,int>,string> apply)
        {
            using var marker=Process.Start(new ProcessStartInfo(exe,"-t 127.0.0.1"){
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})!;
            try
            {
                Check(!marker.HasExited,"Steam fixture started");
                bool removed=false;
                string result=apply((phase,_,_)=>{
                    if(phase!="等待 Steam 退出")return;
                    File.Delete(info);
                    if(!marker.HasExited){marker.Kill();marker.WaitForExit();}
                    removed=true;
                });
                Check(removed,"cache disappeared after pre-shutdown capture");
                return result;
            }
            finally {if(!marker.HasExited){marker.Kill();marker.WaitForExit();}}
        }

        string backup=RunOnExit(progress=>SteamLibrary.Apply(location,[new(1100001,"退出后仍能改名",null)],data,false,progress));
        Check(Directory.Exists(backup) && SteamCache.Read(root,[]).Any(row=>row.AppId==1100001 && row.Name=="退出后仍能改名"),"successful rename");
        SteamLibrary.Restore(location,backup,false,data);
        Check(File.ReadAllBytes(info).SequenceEqual(File.ReadAllBytes(fixtureCache)),"rollback retained original cache bytes");

        var original=SteamCache.Read(root,[]).Single(row=>row.AppId==1100001);
        RecommendedEdit proposed=new(new(original.AppId,null,"退出后也能分类"),original.Name,[]);
        string group=RunOnExit(progress=>SteamLibrary.ApplyRecommended(location,data,false,[proposed],false,()=>{},snapshot=>{
            var game=SteamCache.Read(root,[],new HashSet<int>{original.AppId},snapshot).Single();
            return [proposed with { CurrentName=game.Name }];
        },progress));
        Check(!File.Exists(info) && Directory.Exists(group) &&
            SteamLibrary.Memberships(location,data)[original.AppId].Single()=="退出后也能分类","classification reads snapshot without recreating cache");
        Console.WriteLine("PASS rename and category when Steam removes cache during shutdown");
    }
}
