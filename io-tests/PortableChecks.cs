using System.IO.Compression;
using System.Text.Json;
using SteamUnify;

internal static class PortableChecks
{
    internal static void Run(string test,string fixtureCache)
    {
        void Check(bool ok,string reason){if(!ok)throw new Exception("portable: "+reason);}
        var original=File.ReadAllBytes(fixtureCache);
        (SteamLocation Location,string Data,string Cache,string Acf) Site(string site)
        {
            string folder=Path.Combine(test,site);
            var location=new SteamLocation(Path.Combine(folder,"Steam"),"42");
            string data=Path.Combine(folder,"data");
            string cache=Path.Combine(location.Root,Path.GetFileName(fixtureCache).StartsWith("appinfo_")?
                Path.Combine("appcache","appinfo",Path.GetFileName(fixtureCache)):Path.Combine("appcache","appinfo.vdf"));
            string acf=Path.Combine(location.Root,"steamapps","appmanifest_1100001.acf");
            Directory.CreateDirectory(Path.Combine(location.Root,"userdata","42","config","cloudstorage"));
            foreach(string directory in new[]{data,Path.GetDirectoryName(cache)!,Path.GetDirectoryName(acf)!})Directory.CreateDirectory(directory);
            File.WriteAllBytes(cache,original);
            File.WriteAllText(acf,""" "AppState" { "appid" "1100001" "name" "原清单名称" } """);
            return(location,data,cache,acf);
        }

        var from=Site("portable-from");
        string settings=Path.Combine(from.Data,"desktop.json");
        File.WriteAllText(settings,JsonSerializer.Serialize(new{Root=from.Location.Root,Account="42",RestartSteam=false}));
        File.WriteAllText(Path.Combine(from.Data,"suggestions.v2.json"),"""{"1100001":{"ManualName":"手选译名","ManualGroup":"测试分类"}}""");
        string legacy=Path.Combine(from.Data,"backups","20260101-010000");
        Directory.CreateDirectory(Path.Combine(legacy,"userdata","42","config"));
        File.WriteAllText(Path.Combine(legacy,"manifest.json"),JsonSerializer.Serialize(new{account="42",steam_root=from.Location.Root}));
        File.WriteAllText(Path.Combine(legacy,"userdata","42","config","localconfig.vdf"),"legacy archive marker");
        string native=SteamLibrary.Apply(from.Location,[new(1100001,"迁移前译名","测试分类")],from.Data,false);
        string bundle=Path.Combine(test,"portable.zip");
        Check(Directory.Exists(native) && SteamLibrary.ExportPortable(from.Location,from.Data,bundle)==2,"export native and legacy snapshots");

        var into=Site("portable-to");
        SteamLibrary.Apply(into.Location,[new(1100001,"本机已有名","另一分类")],into.Data,false);
        var beforeImport=File.ReadAllBytes(into.Cache);
        string destinationPrefs=Path.Combine(into.Data,"suggestions.v2.json");
        File.WriteAllText(destinationPrefs,"{}");
        try
        {
            SteamLibrary.ImportPortable(into.Location with {Account="99"},into.Data,bundle,out _);
            throw new Exception("accepted another account archive");
        }
        catch(InvalidDataException){Check(File.ReadAllText(destinationPrefs)=="{}","wrong account leaves preferences unchanged");}

        string bad=Path.Combine(test,"malformed.zip");
        File.Copy(bundle,bad);
        using(var archive=ZipFile.Open(bad,ZipArchiveMode.Update))
            foreach(var entry in archive.Entries.Where(entry=>entry.Name=="file-00000.bin").ToArray()) entry.Delete();
        try
        {
            SteamLibrary.ImportPortable(into.Location,into.Data,bad,out _);
            throw new Exception("accepted incomplete backup");
        }
        catch(InvalidDataException){Check(File.ReadAllText(destinationPrefs)=="{}","incomplete package staged without applying");}

        Check(SteamLibrary.ImportPortable(into.Location,into.Data,bundle,out var rollback)==2 && File.Exists(rollback),"import stages both snapshots and saves previous data");
        Check(beforeImport.SequenceEqual(File.ReadAllBytes(into.Cache)),"import leaves live Steam files untouched");
        using(var options=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(into.Data,"desktop.json"))))
            Check(options.RootElement.GetProperty("Root").GetString()==into.Location.Root,"settings rebased to new machine");
        Check(File.ReadAllText(destinationPrefs).Contains("ManualName"),"suggestions imported");
        var backups=SteamLibrary.Backups(into.Location,into.Data);
        Check(backups.Count==3 && backups.Any(path=>File.Exists(Path.Combine(path,"manifest.json"))),"legacy and native backups listed");
        string migrated=backups.Single(path=>Path.GetFileName(path)==Path.GetFileName(native));
        SteamLibrary.Restore(into.Location,migrated,false,into.Data);
        Check(original.SequenceEqual(File.ReadAllBytes(into.Cache)) &&
            File.ReadAllText(into.Acf).Contains("原清单名称"),"native backup payload restores after paths are rebased");
        Console.WriteLine("PASS portable ZIP native/legacy snapshots, different Steam path, backup before import, wrong account, malformed ZIP");
    }
}
