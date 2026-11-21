"""Build isolated library data for desktop acceptance. All entries and collections are synthetic."""
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import zlib
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
ROOT.mkdir(parents=True, exist_ok=True)
rows = [
    (367520, "Hollow Knight", "空洞骑士", [1], [1625,1628,19]),
    (646570, "Slay the Spire", "杀戮尖塔", [2], [14139,1716]),
    (413150, "Stardew Valley", "星露谷物语", [28], [87918]),
    (620, "Portal 2", "传送门 2", [25], [1664]),
    (105600, "Terraria", "泰拉瑞亚", [1], [1100689,3810]),
    (1086940, "Baldur's Gate 3", "博德之门 3", [3], [122]),
    (1551360, "Forza Horizon 5", "极限竞速：地平线 5", [9], [699]),
    (774171, "Muse Dash", "喵斯快跑", [4], [1621,1752]),
    (1145360, "Hades", "哈迪斯", [1], [19,1716]),
    (524220, "NieR:Automata", "尼尔：机械纪元", [3], [122]),
    (504230, "Celeste", "蔚蓝", [1], [1625]),
    (9000999, "Archive Study", "", [], []),
]
keys = ["appinfo","common","name","type","genres","store_tags","name_localized","schinese","0","1","2"]
keydata = b"".join(x.encode()+b"\0" for x in keys)

def field(t, key, body):
    return bytes([t]) + struct.pack("<I", keys.index(key)) + body

def string(key,text):
    return field(1, key, text.encode()+b"\0")

def inner(key, children):
    return field(0,key,b"".join(children)+b"\x08")

def ints(key,values):
    return inner(key,[field(2,str(i),struct.pack("<i",v)) for i,v in enumerate(values)])

def entry(row):
    aid,en,zh,genres,tags = row
    node = [string("name",en),string("type","Game"),ints("genres",genres),ints("store_tags",tags)]
    if zh:
        node.append(inner("name_localized",[string("schinese",zh)]))
    return inner("appinfo",[inner("common",node)])+b"\x08"

for kind in ("v29","v30","large","workbench","facets","dynamic","workspace","direct","selection","tiny","receipt","extras"):
    root = ROOT / kind
    cache = root/"appcache"
    cloud = root/"userdata/1/config/cloudstorage"
    installed = root/"steamapps"
    for path in (cache, cloud, installed):
        path.mkdir(parents=True,exist_ok=True)
    (cloud.parent/"localconfig.vdf").write_text('"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "646570" { } } } } } }')
    custom = {"id":"personal","name":"我的收藏","added":[646570],"removed":[]}
    (cloud/"cloud-storage-namespace-1.json").write_text(json.dumps([["user-collections.personal",{"version":"2","value":json.dumps(custom,ensure_ascii=False)}]],ensure_ascii=False),encoding="utf-8")
    (cloud/"cloud-storage-namespaces.json").write_text('[[1,"2"]]')
    (installed/"appmanifest_646570.acf").write_text('"AppState"\n{\n "appid" "646570"\n "name" "Slay the Spire"\n}\n')
    if kind=="dynamic":
        # Logs into fixture account 1. Account 2 and unrelated account-1 keys must survive unchanged.
        cloud2=root/"userdata/2/config/cloudstorage"
        cloud2.mkdir(parents=True)
        (cloud2.parent/"localconfig.vdf").write_text('"UserLocalConfigStore" { }')
        (cloud2/"cloud-storage-namespace-1.json").write_bytes((cloud/"cloud-storage-namespace-1.json").read_bytes())
        (cloud2/"cloud-storage-namespaces.json").write_bytes((cloud/"cloud-storage-namespaces.json").read_bytes())
        preserved=json.loads((cloud/"cloud-storage-namespace-1.json").read_text(encoding="utf-8"))
        user_filter={"nFormatVersion":2,"strSearchText":"","filterGroups":[
            {"rgOptions":[3] if i==1 else [],"bAcceptUnion":i in (0,1)} for i in range(9)],"setSuggestions":{}}
        role={"id":"user-rules","name":"现有动态收藏","added":[],"removed":[],"filterSpec":user_filter}
        preserved.append(["user-collections.user-rules",{"version":"6","value":json.dumps(role,ensure_ascii=False)}])
        preserved.append(["client-preference",{"version":"8","value":"preserve me"}])
        (cloud/"cloud-storage-namespace-1.json").write_text(json.dumps(preserved,ensure_ascii=False),encoding="utf-8")
        (cloud/"cloud-storage-namespaces.json").write_text('[[1,"10"],[6,"9"]]')
    games=list(rows)
    if kind=="tiny":
        games=[rows[1]]
    if kind=="extras":
        games=[(646570,"Slay the Spire","备选中文名",[2],[14139,1716])]
    if kind in ("workbench","facets","workspace","direct","selection","receipt"):
        games = [rows[1],
            (9001101, "Amber Archive", "", [], []),
            (9001102, "Birch Archive", "白桦手记", [], []),
            (9001103, "Cinder Archive", "", [2], [14139]),
            (9001104, "Dusk Archive", "薄暮手记", [1], [19]),
            (9001105, "Elm Archive", "榆木手记", [3], [122]),
            (9001106, "Flint Field Notes", "", [3], [122]),
        ]
        if kind in ("facets","workspace","direct","selection","receipt"):
            games.append((9001107, "Frost Notebook", "", [], []))
            for aid, name, *_ in [games[3], games[4]]:
                (installed/f"appmanifest_{aid}.acf").write_text(f'"AppState" {{ "appid" "{aid}" "name" "{name}" }}')
        if kind=="direct":
            owned=" ".join(f'"{app[0]}" {{ }}' for app in games)
            (cloud.parent/"localconfig.vdf").write_text('"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { '+owned+' } } } } }')
            games.append((9001199,"Unowned Cache Entry","无关的缓存条目",[2],[14139]))
    if kind=="large":
        for i in range(2500-len(games)):
            games.append((3900000+i,f"Library Game {i:04}",f"模拟游戏 {i:04}",[2],[14139]))
    if kind!="v30":
        def v29_bytes(library):
            records=[]
            for row in library:
                data=entry(row)
                records.append(struct.pack("<IIII",row[0],60+len(data),1,1)+bytes(32)+hashlib.sha1(data).digest()+data)
            payload=b"".join(records)
            return struct.pack("<IIQ",0x07564429,1,16+len(payload))+payload+struct.pack("<I",len(keys))+keydata
        (cache/"appinfo.vdf").write_bytes(v29_bytes(games))
        if kind=="direct":
            newer=[(aid,en,"白桦新篇" if aid==9001102 else zh,genres,tags) for aid,en,zh,genres,tags in games]
            (ROOT/"direct-updated.vdf").write_bytes(v29_bytes(newer))
    else:
        (cache/"appinfo").mkdir(exist_ok=True)
        index=b""
        payload=b""
        for row in games:
            data=entry(row)
            blob=hashlib.sha1(data).digest()+data
            index+=struct.pack("<III",row[0],len(payload),len(blob))+bytes(69)
            payload+=blob
        head=struct.pack("<10I",0x0756442a,1,1,256,len(games),len(index),len(keys),len(keydata),len(payload),zlib.crc32(index+keydata))
        (cache/"appinfo/appinfo_01.vdf").write_bytes(head+struct.pack("<I",zlib.crc32(head))+index+keydata+payload)
print("Isolated Steam libraries generated:", ROOT)
if "--replay" in sys.argv[2:]:
    tests=Path(__file__).resolve().parent
    app=tests.parent/"SteamUnify2"
    source=ROOT/"replay-src"
    source.mkdir()
    for glob in ("*.xaml","*.cs","*.csproj","*.ico","*.manifest"):
        for file in app.glob(glob):
            shutil.copy2(file,source/file.name)
    shutil.copytree(app/"Resources",source/"Resources")
    shutil.copy2(tests/"ReplayHost.cs",source/"ReplayHost.cs")
    subprocess.run([os.environ.get("DOTNET_EXE","dotnet"),"publish",str(source/"SteamUnify.csproj"),
        "-c","Release","-r","win-x64","--self-contained","true","-p:PublishSingleFile=true",
        "-p:StartupObject=SteamUnify.ReplayHost","-o",str(ROOT/"replay-publish")],check=True)
