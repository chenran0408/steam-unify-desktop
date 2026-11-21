using SteamUnify;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.FirstOrDefault()=="--benchmark-write") { WriteTimings.Run(); return; }

string test = Path.Combine(Path.GetTempPath(),"steam-unify-io-" + Guid.NewGuid().ToString("N"));
string root = Path.Combine(test,"Steam");
string extra = Path.Combine(test,"库");
string data = Path.Combine(test,"Data");
string config = Path.Combine(root,"userdata","42","config");
string cloud = Path.Combine(config,"cloudstorage");
string ns = Path.Combine(cloud,"cloud-storage-namespace-1.json");
string vs = Path.Combine(cloud,"cloud-storage-namespaces.json");
string user = """["user-collections.uc-user",{"key":"user-collections.uc-user","version":"2","value":"{\"id\":\"uc-user\",\"name\":\"策略\",\"added\":[500],\"removed\":[]}"}]""";
string original = "[" + user + "]";
string acf = Path.Combine(extra,"steamapps","appmanifest_1100001.acf");
string oldName = "//comment\n\"AppState\" {\n\t\"nested\" { \"name\" \"ignore\" }\n    \"appid\" \"1100001\"\n    \"name\" \"手动\"\n}";
string fixture = Path.GetFullPath(args[0]);
string inputFile = Directory.EnumerateFiles(Path.Combine(fixture,"appcache"),"*.vdf",SearchOption.AllDirectories).First(p => new FileInfo(p).Length > 44);
string info = Path.Combine(root,Path.GetRelativePath(fixture,inputFile));
void Check(bool value,string purpose) { if(!value) throw new Exception(purpose); }
try
{
    foreach (var folder in new[]{cloud,Path.GetDirectoryName(info),Path.Combine(root,"steamapps"),Path.GetDirectoryName(acf),data}) Directory.CreateDirectory(folder!);
    File.WriteAllText(ns,original);
    File.WriteAllText(vs,"[[1,\"8\"]]");
    File.WriteAllText(acf,oldName,new UTF8Encoding(true));
    File.Copy(inputFile,info);
    File.WriteAllText(Path.Combine(root,"steamapps","libraryfolders.vdf"),"\"libraryfolders\" { \"1\" { \"path\" \"" + extra.Replace("\\","\\\\") + "\" } }");
    File.WriteAllText(Path.Combine(config,"localconfig.vdf"),""" "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "1100001" {} } } } } "WebStorage" {"LocalizedTagNames_schinese" "{\"tags\":[{\"tagid\":100,\"name\":\"射击\"}]}"} } """);
    var loc = SteamLibrary.Detect(root,"42");
    var meta = SteamLibrary.Inspect(loc);
    Check(meta.Owned.SetEquals([1100001]) && meta.TagNames[100]=="射击" && meta.Installed[1100001] == acf,"inspect");
    RecommendedChecks.Run(loc,data,info);
    ShutdownChecks.Run(test,inputFile);
    PortableChecks.Run(test,inputFile);
    ParallelStagingChecks.Run(test);
    DynamicCollectionChecks.Run(root, data);
    var bak = SteamLibrary.Apply(loc,[new(1100001,"中文\"名\\称","策略"),new(1100002,null,"策略"),new(1100003,null,"动作")],data,false);
    var memb = SteamLibrary.Memberships(loc,data);
    var scanned = SteamCache.Read(root,new()).ToDictionary(g=>g.AppId);
    Check(SteamLibrary.OriginalNames(loc,data,scanned)[1100001]=="Test Game 1100001","search name survives apply");
    Check(memb[1100001].Single()=="策略" && memb[1100002].Single()=="策略" && memb[1100003].Single()=="动作","membership");
    Check(File.ReadAllText(acf).Contains("\"ignore\"") && File.ReadAllText(acf).Contains("中文\\\"名\\\\称"),"quote escapes");
    Check(File.ReadAllBytes(acf).Take(3).SequenceEqual(new byte[]{0xEF,0xBB,0xBF}),"BOM");
    Check(JsonNode.DeepEquals(JsonNode.Parse(user),JsonNode.Parse(File.ReadAllText(ns))![0]),"user collection unchanged");
    Check(File.Exists(Path.Combine(bak,"snapshot.json")) && SteamLibrary.Backups(loc,data).Contains(bak),"backup listing");
    var noChange = SteamLibrary.Apply(loc,[new(1100001,null,"策略")],data,false);
    Check(noChange.Length==0,"idempotent");
    var before = new Dictionary<string,byte[]> { [info]=File.ReadAllBytes(info), [acf]=File.ReadAllBytes(acf),[ns]=File.ReadAllBytes(ns),[vs]=File.ReadAllBytes(vs) };
    // Allow copying the last target for backup, then reject its replacement during commit.
    using (var locked = new FileStream(vs,FileMode.Open,FileAccess.Read,FileShare.Read))
    {
        bool failed = false;
        try { SteamLibrary.Apply(loc,[new(1100001,"将回滚","动作")],data,false); }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed,"locked target rejected");
    }
    foreach(var kv in before) Check(File.ReadAllBytes(kv.Key).SequenceEqual(kv.Value),"rollback " + Path.GetFileName(kv.Key));
    SteamLibrary.Apply(loc,[new(1100001,null,"动作"),new(1100003,null,"")],data,false);
    memb = SteamLibrary.Memberships(loc,data);
    Check(memb[1100001].Single()=="动作" && memb[1100002].Single()=="策略" && !memb.ContainsKey(1100003),"incremental reassign");
    SteamLibrary.Restore(loc,bak,false,data);
    Check(File.ReadAllBytes(info).SequenceEqual(File.ReadAllBytes(inputFile)) && File.ReadAllText(acf)==oldName && File.ReadAllText(ns)==original && File.ReadAllText(vs)=="[[1,\"8\"]]","restore originals");
    Check(SteamLibrary.Memberships(loc,data).Count==0,"restore removed new ownership");
    SteamLibrary.Apply(loc,[new(1100001,"我的中文名","策略")],data,false);
    string otherCloud = Path.Combine(root,"userdata","43","config","cloudstorage");
    Directory.CreateDirectory(otherCloud);
    var other = new SteamLocation(root,"43");
    SteamLibrary.Apply(other,[new(1100001,"第二次改名","独立")],data,false);
    string undo = SteamLibrary.UndoManaged(other,data,false);
    var game = SteamCache.Read(root,new()).Single(g=>g.AppId==1100001);
    Check(game.Name=="Test Game 1100001" && game.Localized["schinese"]=="游戏 1100001","undo original name/localizations across accounts");
    Check(File.ReadAllText(acf)==oldName && SteamLibrary.Memberships(other,data).Count==0,"undo original ACF + own collections");
    Check(SteamLibrary.Memberships(loc,data)[1100001].Single()=="策略","undo preserves other account");
    Check(SteamLibrary.UndoManaged(other,data,false)=="","second undo is a no-op");
    SteamLibrary.Restore(other,undo,false,data);
    Check(SteamCache.Read(root,new()).Single(g=>g.AppId==1100001).Name=="第二次改名" && SteamLibrary.Memberships(other,data).Count==1,"undo backup restores both sides");
    SteamLibrary.UndoManaged(loc,data,false);
    Check(SteamCache.Read(root,new()).Single(g=>g.AppId==1100001).Name=="Test Game 1100001","restore also restored undo ledger");
    Check(JsonNode.DeepEquals(JsonNode.Parse(user),JsonNode.Parse(File.ReadAllText(ns))![0]),"undo preserved same-name user-owned collection");
    string legacyData = Path.Combine(test,"LegacyData");
    Directory.CreateDirectory(legacyData);
    var oldBackup = Path.Combine(test,"backups","2026-11-20");
    var oldCloud = Path.Combine(oldBackup,"userdata","42","config","cloudstorage");
    Directory.CreateDirectory(oldCloud);
    File.WriteAllText(Path.Combine(oldCloud,Path.GetFileName(ns)),original);
    File.WriteAllText(Path.Combine(oldBackup,"manifest.json"),JsonSerializer.Serialize(new { account="42", steam_root=root, managed_collections=new Dictionary<string,string>() }));
    string oldRecord = """["user-collections.uc-old",{"key":"user-collections.uc-old","version":"9","value":"{\"id\":\"uc-old\",\"name\":\"动作\",\"added\":[1100001],\"removed\":[]}"}]""";
    File.WriteAllText(ns,"[" + user + "," + oldRecord + "]");
    var oldGame = SteamCache.Read(root,new()).Single(g=>g.AppId==1100001);
    var legacyName = new Dictionary<int,NameChange> { [1100001] = new("旧版中文名",null) };
    foreach (var item in SteamCache.PlanNames(root,legacyName)) File.WriteAllBytes(item.Key,item.Value);
    File.WriteAllText(Path.Combine(legacyData,"state.json"),JsonSerializer.Serialize(new {
        account="42", collections = new Dictionary<string,string> { ["策略"]="uc-user",["动作"]="uc-old" },
        apps = new Dictionary<string,object> { ["1100001"]=new { applied=true,zh_name="旧版中文名",orig_name=oldGame.Name, orig_localized=oldGame.Localized } }
    }));
    Check(SteamLibrary.Memberships(loc,legacyData)[1100001].Single()=="动作","legacy confirmed ownership");
    Check(!SteamLibrary.Memberships(loc,legacyData).ContainsKey(500),"legacy auto-adopted manual set protected");
    Check(SteamLibrary.Backups(loc,legacyData).Contains(oldBackup),"legacy backup discovery");
    SteamLibrary.UndoManaged(loc,legacyData,false);
    Check(SteamCache.Read(root,new()).Single(g=>g.AppId==1100001).Localized["schinese"]=="游戏 1100001","legacy originals recovered");
    Check(JsonNode.DeepEquals(JsonNode.Parse(user),JsonNode.Parse(File.ReadAllText(ns))![0]),"legacy undo protected manual set");
    SteamLibrary.Restore(loc,oldBackup,false,legacyData);
    Check(File.ReadAllText(ns)==original,"restore Python backup");
    Console.WriteLine("PASS detection, installed/owned/tag metadata, name escaping, BOM, same-name user sets, incremental categories, no-op, atomic rollback after locked target, and byte-exact restore");
}
finally { if (Directory.Exists(test)) Directory.Delete(test,true); }

