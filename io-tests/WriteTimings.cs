using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SteamUnify;

internal static class WriteTimings
{
    const int Count=2400, Installed=240;
    internal const int FirstId=15000000;
    static readonly string[] Groups=["动作","角色","策略","休闲","解谜","体育","沙盒"];

    internal static void Run()
    {
        string scratch=Directory.CreateTempSubdirectory("steam-write-timing-").FullName;
        try
        {
            var names=new List<double>();var grouping=new List<double>();var freshTimes=new List<double>();
            var planTimes=new List<double>();
            byte[] fixture=MakeAppInfo();
            var ids=Enumerable.Range(FirstId,Count).ToHashSet();
            for(int iteration=0;iteration<6;iteration++)
            {
                string root=Path.Combine(scratch,"Steam"),data=Path.Combine(scratch,"data");
                Directory.CreateDirectory(Path.Combine(root,"appcache"));
                Directory.CreateDirectory(Path.Combine(root,"userdata","1","config","cloudstorage"));
                Directory.CreateDirectory(Path.Combine(root,"steamapps"));
                File.WriteAllBytes(Path.Combine(root,"appcache","appinfo.vdf"),fixture);
                foreach(int id in ids.Take(Installed))
                    File.WriteAllText(Path.Combine(root,"steamapps",$"appmanifest_{id}.acf"),$"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"Game {id}\" }}");
                var loc=new SteamLocation(root,"1");
                List<RecommendedEdit> ReadPlan(bool translate)
                {
                    var memberships=SteamLibrary.Memberships(loc,data);
                    return SteamCache.Read(root,[],ids).Select(g=>new RecommendedEdit(new(g.AppId,translate?g.Localized["schinese"]:null,
                        translate?null:Groups[g.AppId%Groups.Length]),g.Name,memberships.GetValueOrDefault(g.AppId)??[])).ToList();
                }
                var current=ReadPlan(false);
                var sw=Stopwatch.StartNew();
                var groupBackup=SteamLibrary.ApplyRecommended(loc,data,false,current,false,()=>throw new Exception(),()=>ReadPlan(false));
                if(iteration>0)grouping.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                current=ReadPlan(true);
                if(iteration>0)freshTimes.Add(sw.Elapsed.TotalMilliseconds);
                var requested=current.ToDictionary(r=>r.Edit.AppId,r=>new NameChange(r.Edit.Name!,null));
                sw.Restart();
                var output=SteamCache.PlanNames(root,requested).Single();
                if(iteration>0)planTimes.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                var renameBackup=SteamLibrary.ApplyRecommended(loc,data,false,current,false,()=>throw new Exception(),()=>ReadPlan(true));
                if(iteration>0)names.Add(sw.Elapsed.TotalMilliseconds);
                if(!Directory.Exists(groupBackup)||!Directory.Exists(renameBackup)||!File.ReadAllBytes(output.Key).SequenceEqual(output.Value))
                    throw new Exception("Unexpected benchmark write");
                if(iteration==0)Console.WriteLine("Renamed cache SHA256 "+Convert.ToHexString(SHA256.HashData(output.Value)));
                Directory.Delete(root,true);Directory.Delete(data,true);
            }
            static double Median(List<double> values) => values.Order().ElementAt(values.Count/2);
            Console.WriteLine($"{Count} games, {Installed} installed manifests, {MakeSize(fixture.Length)} MiB v29, median of 5 warm runs (ms): fresh={Median(freshTimes):F1}, cache patch+verify={Median(planTimes):F1}, recommended group incl backup={Median(grouping):F1}, recommended names incl backup={Median(names):F1}");
        }
        finally {Directory.Delete(scratch,true);}
    }
    static string MakeSize(int bytes) => (bytes/1048576d).ToString("F1");

    internal static byte[] MakeAppInfo(int count=Count,int padding=4096)
    {
        string[] keys=["appinfo","common","name","type","name_localized","schinese","extended","description"];
        using var stream=new MemoryStream();
        using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        writer.Write(0x07564429);writer.Write(1);writer.Write(0L);
        string filler=new('x',padding);
        for(int id=FirstId;id<FirstId+count;id++)
        {
            using var entry=new MemoryStream();using var kv=new BinaryWriter(entry);
            void Field(byte type,int key){kv.Write(type);kv.Write(key);}
            void Text(int key,string value){Field(1,key);kv.Write(Encoding.UTF8.GetBytes(value));kv.Write((byte)0);}
            Field(0,0);Field(0,1);Text(2,$"Game {id}");Text(3,"Game");Field(0,4);Text(5,$"游戏 {id}");kv.Write((byte)8);kv.Write((byte)8);
            Field(0,6);Text(7,filler);kv.Write((byte)8);kv.Write((byte)8);kv.Write((byte)8);
            byte[] body=entry.ToArray();
            writer.Write(id);writer.Write(60+body.Length);writer.Write(new byte[40]);writer.Write(SHA1.HashData(body));writer.Write(body);
        }
        long keyOffset=stream.Position;
        writer.Write(keys.Length);foreach(var key in keys){writer.Write(Encoding.UTF8.GetBytes(key));writer.Write((byte)0);}
        stream.Position=8;writer.Write(keyOffset);
        return stream.ToArray();
    }
}
