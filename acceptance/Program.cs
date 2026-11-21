using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using El = System.Windows.Automation.AutomationElement;

internal static class Program
{
    [DllImport("user32")] static extern bool SetProcessDpiAwarenessContext(IntPtr id);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr hwnd, int n);
    [DllImport("user32")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
    [DllImport("user32")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    [DllImport("user32")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32")] static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
    private delegate bool EnumProc(IntPtr handle,IntPtr param);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint id);
    [DllImport("user32",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr handle,StringBuilder title,int length);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int X,Y,Right,Bottom; }
    static int pid;
    static readonly List<string> log = new();
    static string logPath="";
    static El main=null!;
    static int pointerActivations;
    static void Log(string s){Console.WriteLine(s);log.Add(s);File.WriteAllLines(logPath,log);}
    static void Assert(bool ok,string msg){if(!ok)throw new Exception(msg);}
    static void Until(Func<bool> f,string why,int seconds=15)
    {
        var t=Stopwatch.StartNew();
        while(t.Elapsed.TotalSeconds<seconds)
        {
            try{if(f())return;} catch(ElementNotAvailableException){} catch(InvalidOperationException){}
            Thread.Sleep(70);
        }
        throw new TimeoutException(why+"; windows "+string.Join(" | ",Windows().Select(e=>e.Current.Name)));
    }
    static List<El> Windows()
    {
        var windows=new List<El>();
        EnumWindows((h,_)=>{
            GetWindowThreadProcessId(h,out uint id);
            if(id==pid)
            {
                var t=new StringBuilder(255);
                if(GetWindowText(h,t,t.Capacity)>0)
                    try{windows.Add(El.FromHandle(h));}catch(ElementNotAvailableException){}
            }
            return true;
        },IntPtr.Zero);
        return windows;
    }
    static El Window(string title)
    {
        El? result=null;
        if(title=="Steam 统译")
            Until(()=> (result=Windows().FirstOrDefault(e=>e.Current.Name==title))!=null,"main window");
        else
        {
            Until(()=>{
                result=Find(main,El.AutomationIdProperty,"InPageHost");
                return result!=null && !result.Current.IsOffscreen && result.Current.Name==title;
            },"task page "+title);
            Assert(!Windows().Any(e=>e.Current.ControlType==ControlType.Window && e.Current.NativeWindowHandle!=main.Current.NativeWindowHandle &&
                !e.Current.IsOffscreen),"secondary native window instead of embedded "+title);
        }
        return result!;
    }
    static void ActivateFixture()
    {
        var handle=(IntPtr)main.Current.NativeWindowHandle;
        ShowWindow(handle,9);
        SetWindowPos(handle,(IntPtr)(-1),0,0,0,0,3);
        var foreground=GetWindowThreadProcessId(GetForegroundWindow(),out _);
        var runner=GetCurrentThreadId();
        var attached=foreground!=0 && foreground!=runner && AttachThreadInput(runner,foreground,true);
        try{SetForegroundWindow(handle);}
        finally{if(attached)AttachThreadInput(runner,foreground,false);}
    }
    static El? Find(El at, AutomationProperty p, object value)=>at.FindFirst(TreeScope.Descendants,new PropertyCondition(p,value));
    static bool IsVisible(El at,AutomationProperty property,string value)
    {
        var element=Find(at,property,value);
        return element!=null && !element.Current.IsOffscreen;
    }
    static int VisibleMachineSources(El at) => at.FindAll(TreeScope.Descendants,
        new AndCondition(new PropertyCondition(El.NameProperty,"机器翻译"),new PropertyCondition(El.ControlTypeProperty,ControlType.Text)))
        .Cast<El>().Count(e=>!e.Current.IsOffscreen);
    static El Id(El at,string id)=>Find(at,El.AutomationIdProperty,id)??throw new InvalidOperationException("missing id "+id);
    static El Button(El at,string name)=>at.FindFirst(TreeScope.Descendants,new AndCondition(
        new PropertyCondition(El.NameProperty,name),new PropertyCondition(El.ControlTypeProperty,ControlType.Button)))
        ??throw new Exception("missing button "+name);
    static void Click(El e)=>((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    static void PointerClick(El element)
    {
        GetWindowThreadProcessId(GetForegroundWindow(),out uint active);
        if(active!=pid)ActivateFixture();
        var point=new System.Windows.Point();
        Until(()=>{
            if(element.TryGetClickablePoint(out point))return true;
            var rect=element.Current.BoundingRectangle;
            if(rect.IsEmpty || element.Current.IsOffscreen)return false;
            point=new System.Windows.Point(rect.Left+rect.Width/2,rect.Top+rect.Height/2);
            var hit=El.FromPoint(point);
            if(element.Current.ControlType==ControlType.ListItem && hit.Current.ProcessId==pid)return true;
            while(hit!=null && hit.Current.ProcessId==pid)
            {
                if(hit.Equals(element))return true;
                hit=TreeWalker.ControlViewWalker.GetParent(hit);
            }
            return false;
        },"visible pointer target "+element.Current.Name);
        System.Windows.Forms.Cursor.Position=new Point((int)point.X,(int)point.Y);
        mouse_event(2,0,0,0,UIntPtr.Zero);
        mouse_event(4,0,0,0,UIntPtr.Zero);
        pointerActivations++;
    }
    static bool Ready()=>main.Current.IsEnabled && IsVisible(main,El.AutomationIdProperty,"LibraryGrid") && Id(main,"MoreButton").Current.IsEnabled;
    static El PopupItem(AutomationProperty property,string key)
    {
        El? item=null;
        Until(()=>{
            foreach(El root in El.RootElement.FindAll(TreeScope.Children,new PropertyCondition(El.ProcessIdProperty,pid)))
            {
                item=root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(El.ControlTypeProperty,ControlType.MenuItem),
                    new PropertyCondition(property,key),new PropertyCondition(El.IsOffscreenProperty,false)));
                if(item!=null)return true;
            }
            return false;
        },"visible menu command "+key);
        return item!;
    }
    static void MoreAction(string id)
    {
        El? menu=null;
        Until(()=>(menu=Find(main,El.AutomationIdProperty,"MoreButton"))!=null && !menu.Current.IsOffscreen && menu.Current.IsEnabled,"main menu ready for "+id);
        PointerClick(menu!);
        var item=PopupItem(El.AutomationIdProperty,id);
        Assert(item.Current.IsEnabled,"disabled menu command "+id);
        PointerClick(item);
    }
    static void ShowFilters()
    {
        if(!IsVisible(main,El.AutomationIdProperty,"InstalledFilter"))PointerClick(Id(main,"FilterButton"));
        Until(()=>IsVisible(main,El.AutomationIdProperty,"InstalledFilter"),"show library filters");
    }
    static bool IsPage(string label)
    {
        var active=((SelectionPattern)Id(main,"Navigation").GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
        return active.Length==1 && TreeText(active[0]).Contains(label);
    }
    static void SetCheck(string id,bool value)
    {
        if(id=="InstalledFilter")ShowFilters();
        var box=Id(main,id);var p=(TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern);
        if(value!=(p.Current.ToggleState==ToggleState.On))p.Toggle();
    }
    static void Search(string s)=>((ValuePattern)Id(main,"Search").GetCurrentPattern(ValuePattern.Pattern)).SetValue(s);
    static int Count(El grid)=>((GridPattern)grid.GetCurrentPattern(GridPattern.Pattern)).Current.RowCount;
    static int Rows()=>Count(Id(main,"LibraryGrid"));
    static string TreeText(El element)=>string.Join(" | ",element.FindAll(TreeScope.Descendants,Condition.TrueCondition)
        .Cast<El>().Select(e=>e.Current.Name).Where(s=>s.Length>0));
    static string Sha(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Screenshot(El element,string path)
    {
        var h=(IntPtr)element.Current.NativeWindowHandle;
        if(h==IntPtr.Zero)h=(IntPtr)main.Current.NativeWindowHandle;
        ShowWindow(h,9); SetForegroundWindow(h);
        Thread.Sleep(280);
        GetWindowRect(h,out var r);
        using var b=new Bitmap(r.Right-r.X,r.Bottom-r.Y);
        using var g=Graphics.FromImage(b);
        var dc=g.GetHdc();
        bool printed;
        try{printed=PrintWindow(h,dc,2);}finally{g.ReleaseHdc(dc);}
        if(!printed || b.GetPixel(b.Width/2,b.Height/2).ToArgb()==Color.Black.ToArgb())
            g.CopyFromScreen(r.X,r.Y,0,0,b.Size);
        b.Save(path,ImageFormat.Png);
    }
    static void CopyApp(string publish,string app)
    {
        foreach(string file in new[]{"official_names.json","heybox_names.json","category_model.json","genres_zh.json","tag_names_zh.json"})
            Assert(File.Exists(Path.Combine(publish,"Resources",file)),"published package is missing Resources/"+file);
        Directory.CreateDirectory(app);
        foreach(var file in Directory.EnumerateFiles(publish,"*",SearchOption.AllDirectories))
        {
            string rel=Path.GetRelativePath(publish,file);
            if(rel.StartsWith("data"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
            string to=Path.Combine(app,rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file,to,true);
        }
    }
    static void NarrowScreenshot(string baseDir,string filename="main-narrow.png",string[]? controls=null)
    {
        var hwnd=(IntPtr)main.Current.NativeWindowHandle;
        GetWindowRect(hwnd,out var original);
        double scale=GetDpiForWindow(hwnd)/96d;
        // Keep this short desktop hit-test above other windows while checking physical pointer coordinates.
        SetWindowPos(hwnd,(IntPtr)(-1),0,0,(int)Math.Ceiling(850*scale),(int)Math.Ceiling(600*scale),0x2);
        try
        {
            Screenshot(main,Path.Combine(baseDir,filename));
            var window=main.Current.BoundingRectangle;
            Assert(Math.Abs(window.Width/scale-850)<3,"window did not resize to 850 DIP");
            foreach(string id in controls??new[]{"FilterButton","RowEdit_9000999","ClassifyButton","TranslateButton","BatchSelectButton","PreviewButton","CreateAllGamesButton"})
            {
                var element=Id(main,id);
                var area=element.Current.BoundingRectangle;
                Assert(area.Width>0 && area.Height>0 && area.Left>=window.Left && area.Right<=window.Right &&
                    area.Top>=window.Top && area.Bottom<=window.Bottom,"control clipped at 850 DIP: "+id);
                if(id is "ClassifyButton" or "TranslateButton" or "BatchSelectButton")
                {
                    Until(()=>{
                        if(!element.TryGetClickablePoint(out var point))return false;
                        var hit=El.FromPoint(point);
                        while(hit!=null && hit.Current.AutomationId!=id)hit=TreeWalker.ControlViewWalker.GetParent(hit);
                        return hit!=null;
                    },"visible 850-DIP pointer target for "+id,3);
                }
            }
        }
        finally{SetWindowPos(hwnd,(IntPtr)(-1),0,0,original.Right-original.X,original.Bottom-original.Y,0x2);}
    }
    static void Restore()
    {
        MoreAction("BackupsButton");var backups=Window("备份与还原");
        Click(Button(backups,"还原所选备份"));
        var confirm=Window("确认还原");
        var yes=Id(confirm,"ConfirmRecovery");
        Click(yes);
    }
    static void ClickCell(El grid,int row,byte modifier=0)
    {
        // WPF returns a virtual GridItem peer after focus changes; use the true row and column bounds.
        var ordinalCell=((GridPattern)grid.GetCurrentPattern(GridPattern.Pattern)).GetItem(row,0);
        var box=Find(ordinalCell,El.ControlTypeProperty,ControlType.CheckBox)!;
        var head=grid.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.HeaderItem)).Cast<El>()
            .Single(e=>e.Current.Name=="中文名");
        var rowRect=box.Current.BoundingRectangle;
        var columnRect=head.Current.BoundingRectangle;
        var point=new System.Windows.Point(columnRect.Left+columnRect.Width/2,rowRect.Top+rowRect.Height/2);
        ActivateFixture();
        Until(()=>!rowRect.IsEmpty && !columnRect.IsEmpty && El.FromPoint(point).Current.ProcessId==pid,"physical Chinese-name cell inside fixture window");
        if(modifier!=0)keybd_event(modifier,0,0,UIntPtr.Zero);
        try
        {
            System.Windows.Forms.Cursor.Position=new Point((int)point.X,(int)point.Y);
            mouse_event(2,0,0,0,UIntPtr.Zero);
            mouse_event(4,0,0,0,UIntPtr.Zero);
            pointerActivations++;
        }
        finally{if(modifier!=0)keybd_event(modifier,0,2,UIntPtr.Zero);}
    }
    static void PickFirstTwo()
    {
        var grid=Id(main,"LibraryGrid");
        ClickCell(grid,0);ClickCell(grid,1,0x11);
        Until(()=>Id(main,"SelectionSummary").Current.Name.Contains("2 / 12"),"ordinary click + Ctrl selects just the two batch targets");
        Assert(!IsVisible(main,El.AutomationIdProperty,"EditButton"),"library exposes a second persistent editor action");
    }
    static void Advanced(string root,string data,TcpListener proxy)
    {
        PickFirstTwo();
        var steps=pointerActivations;
        MoreAction("BulkEditButton");var edit=Window("批量编辑 2 款游戏");
        Assert(pointerActivations-steps==2,"one menu and one command open checked batch edit");
        var group=Find(edit,El.ControlTypeProperty,ControlType.ComboBox)!;
        ((ValuePattern)group.GetCurrentPattern(ValuePattern.Pattern)).SetValue("测试收藏集");
        var pin=Id(edit,"PinGroup");
        var toggle=(TogglePattern)pin.GetCurrentPattern(TogglePattern.Pattern);
        if(toggle.Current.ToggleState==ToggleState.Off)toggle.Toggle();
        Click(Button(edit,"保存"));
        string prefs=Path.Combine(data,"suggestions.v2.json");
        int ManualCount()
        {
            if(!File.Exists(prefs))return -1;
            using var doc=JsonDocument.Parse(File.ReadAllBytes(prefs));
            return doc.RootElement.EnumerateObject().Count(p=>p.Value.TryGetProperty("ManualGroup",out var v)&&v.GetString()=="测试收藏集");
        }
        Until(()=>ManualCount()==2 && Ready(),"bulk manual classifications saved");
        using(var doc=JsonDocument.Parse(File.ReadAllBytes(prefs)))
        {
            var pinned=doc.RootElement.EnumerateObject().Where(p=>p.Value.TryGetProperty("ManualGroup",out var g) && g.GetString()=="测试收藏集")
                .Select(p=>p.Name).ToHashSet();
            Assert(pinned.SetEquals(new[]{"9000999","1086940"}),"batch edited a highlighted or hidden game");
        }
        MoreAction("BulkEditButton");
        edit=Window("批量编辑 2 款游戏");Click(Button(edit,"恢复自动建议"));
        Until(()=>ManualCount()==0 && Ready(),"bulk classifications unlocked");
        Log("A standard row click plus Ctrl selected just the two games subsequently edited and unlocked.");
        Search("9000999");Until(()=>Rows()==1,"select missing name for lookup");
        var prior=FilesIn(root);
        PointerClick(Id(main,"TranslateButton"));
        Until(()=>proxy.Pending() && Id(main,"StopButton").Current.IsEnabled,"slow local proxy request and cancel action");
        AssertProgress();
        Until(()=>Id(main,"StatusText").Current.Name.Contains("已找到"),"online progress");
        Click(Id(main,"StopButton"));
        Until(()=>Ready() && Id(main,"StatusText").Current.Name.Contains("已停止") &&
            !IsVisible(main,El.AutomationIdProperty,"TaskProgress"),"cancel pending HTTP lookup and hide its progress");
        Assert(prior.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==0,"canceling a pending lookup wrote Steam or created a backup");
        Log("Direct translation reported progress and canceled an offline proxy request without changing Steam files.");
    }
    static void UndoAll(string root)
    {
        var acf=Path.Combine(root,"steamapps","appmanifest_646570.acf");
        var cache=Path.Combine(root,"appcache","appinfo","appinfo_01.vdf");
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var originalAcf=Sha(acf);
        var originalCache=Sha(cache);
        using var original=JsonDocument.Parse(File.ReadAllBytes(ns));
        string personal=original.RootElement[0][1].GetProperty("value").GetString()!;
        Search("646570");Until(()=>Rows()==1,"filter game for undo");
        Click(Id(main,"PreviewButton"));Click(Button(Window("预览更改"),"应用到 Steam"));
        Until(()=>Sha(acf)!=originalAcf && Sha(cache)!=originalCache && Ready(),"combined apply ready");
        MoreAction("BackupsButton");Click(Button(Window("备份与还原"),"撤销历次整理"));
        var confirm=Window("撤销历次整理");
        Click(Id(confirm,"ConfirmRecovery"));
        Until(()=>Sha(acf)==originalAcf && Sha(cache)==originalCache && Ready(),"undo original name/ACF");
        using var restored=JsonDocument.Parse(File.ReadAllBytes(ns));
        var entries=restored.RootElement.EnumerateArray().ToList();
        Assert(entries.Any(e=>e[0].ToString()=="user-collections.personal" && e[1].GetProperty("value").GetString()==personal),"original collection lost on undo");
        Assert(entries.Any(e=>e[1].TryGetProperty("is_deleted",out var v) && v.GetBoolean()),"managed collection not removed");
        Log("Confirmed combined write then managed undo on v30; original bytes and user's own collection recovered.");
    }

    static void Navigate(string label, int expected)
    {
        var item=Find(Id(main,"Navigation"),El.NameProperty,label)
            ??throw new Exception("missing navigation "+label);
        while(!item.TryGetCurrentPattern(SelectionItemPattern.Pattern,out _))
            item=TreeWalker.ControlViewWalker.GetParent(item);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Until(()=>IsPage(label) && Rows()==expected, label+" view with "+expected+" rows");
    }
    static void Toggle(El e,bool value)
    {
        var toggle=(TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern);
        if((toggle.Current.ToggleState==ToggleState.On)!=value || toggle.Current.ToggleState==ToggleState.Indeterminate)
            toggle.Toggle();
        Until(()=>toggle.Current.ToggleState==(value?ToggleState.On:ToggleState.Off),"set "+e.Current.AutomationId+" "+value);
    }
    static Dictionary<int,byte[]> V29Records(string path)
    {
        byte[] content=File.ReadAllBytes(path);
        int end=checked((int)BitConverter.ToInt64(content,8));
        var records=new Dictionary<int,byte[]>();
        for(int offset=16;offset<end;)
        {
            int app=BitConverter.ToInt32(content,offset);
            int size=checked(BitConverter.ToInt32(content,offset+4)+8);
            records.Add(app,content.AsSpan(offset,size).ToArray());
            offset+=size;
        }
        return records;
    }
    static SortedDictionary<string,string> FilesIn(string root) =>
        new(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(root,f),Sha));
    static void PreviewScope(El preview)
    {
        Toggle(Id(preview,"PreviewAll"),false);
        var apply=Id(preview,"PreviewApply");
        Until(()=>!apply.Current.IsEnabled,"nothing to write disables apply");
        Toggle(Id(preview,"PreviewName_9001104"),true);
        Toggle(Id(preview,"PreviewGroup_9001105"),true);
        Until(()=>{
            var summary=Id(preview,"PreviewSummary").Current.Name;
            return apply.Current.IsEnabled && summary.Contains("2 款") && summary.Contains("1 个名称") && summary.Contains("1 项收藏集");
        },"two games, one name and one collection selected in preview");
    }
    static void TestPending(string root,string baseDir)
    {
        var initial=FilesIn(root);
        Navigate("待应用",6);
        Click(Id(main,"PreviewButton"));var pendingPreview=Window("预览更改");
        Toggle(Id(pendingPreview,"PreviewAllNames"),false);
        Toggle(Id(pendingPreview,"PreviewAllGroups"),false);
        Assert(!Id(pendingPreview,"PreviewApply").Current.IsEnabled,"zero selected fields still permits writing");
        Click(Button(pendingPreview,"返回"));
        Until(()=>Ready() && Rows()==6 && IsPage("待应用"),"preview column choices do not filter the pending list");

        var grid=Id(main,"LibraryGrid");
        grid.SetFocus();
        SetForegroundWindow((IntPtr)main.Current.NativeWindowHandle);
        System.Windows.Forms.SendKeys.SendWait("^f");
        Until(()=>El.FocusedElement.Current.AutomationId=="Search","Ctrl+F focuses search");

        Search("archive 9001104");
        Until(()=>Rows()==1 && TreeText(grid).Contains("Dusk Archive"),"two search words across title and AppID");
        Click(Id(main,"ClearSearchButton"));
        Until(()=>Rows()==6 && IsPage("待应用"),"clear restores pending scope");
        Search("does-not-match");Until(()=>Rows()==0,"no matches");
        Assert(!Id(main,"PreviewButton").Current.IsEnabled && Id(main,"EmptyTitle").Current.Name.Contains("没有找到") &&
            Id(main,"EmptyButton").Current.Name=="清除搜索","empty search has one recovery action and no writable scope");
        Click(Id(main,"EmptyButton"));
        Until(()=>Rows()==6 && IsPage("待应用"),"empty action clears just the search");
        Search("archive");Until(()=>Rows()==4,"only four visible pending games");

        Click(Id(main,"PreviewButton"));
        var preview=Window("预览更改");
        Assert(Find(preview,El.AutomationIdProperty,"PreviewGame_646570")==null,"filtered-out game in preview");
        Assert(!IsVisible(preview,El.AutomationIdProperty,"PreviewName_9001103"),"no suggested name should have no name checkbox");
        Assert(!IsVisible(preview,El.AutomationIdProperty,"PreviewGroup_9001102"),"no collection change should have no collection checkbox");
        Assert(VisibleMachineSources(preview)==0,"official or missing translations were labeled machine-translated");
        var dusk=Id(preview,"PreviewGame_9001104");
        Toggle(dusk,false);
        foreach(var field in new[]{"PreviewName_9001104","PreviewGroup_9001104"})
        {
            var state=(TogglePattern)Id(preview,field).GetCurrentPattern(TogglePattern.Pattern);
            Assert(state.Current.ToggleState==ToggleState.Off,"excluding a game left a pending field");
        }
        Toggle(dusk,true);
        PreviewScope(preview);
        Screenshot(preview,Path.Combine(baseDir,"preview-selected.png"));
        Click(Button(preview,"返回"));
        Until(()=>main.Current.IsEnabled && Id(main,"PreviewButton").Current.IsEnabled,"return from edited preview");
        Assert(initial.SequenceEqual(FilesIn(root)),"canceling per-field preview wrote to Steam");

        var skip=Find(grid,El.NameProperty,"勾选 Birch Archive")??throw new Exception("missing picked checkbox for Birch");
        Toggle(skip,false);
        Click(Id(main,"PreviewButton"));preview=Window("预览更改");
        Assert(Find(preview,El.AutomationIdProperty,"PreviewGame_9001102")==null,"unchecked game in preview");
        Assert(Count(Find(preview,El.ControlTypeProperty,ControlType.DataGrid)!)==3,"preview scope with filtered-out and unchecked games");
        PreviewScope(preview);
        var acf=Path.Combine(root,"steamapps","appmanifest_646570.acf");
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var cache=Path.Combine(root,"appcache","appinfo.vdf");
        var before=V29Records(cache);
        Click(Id(preview,"PreviewApply"));
        Until(()=>Sha(cache)!=initial[Path.GetRelativePath(root,cache)] && Ready(),"disjoint subset apply finished");
        Assert(initial[Path.GetRelativePath(root,acf)]==Sha(acf),"hidden installed game changed");
        var after=V29Records(cache);
        var changed=before.Where(x=>!after[x.Key].SequenceEqual(x.Value)).Select(x=>x.Key).ToList();
        Assert(changed.SequenceEqual(new[]{9001104}),"wrong changed appinfo records: "+string.Join(",",changed));
        var delta=after[9001104];
        const int nameOffset=68+15; // Synthetic root object, common object, then string key.
        Assert(Encoding.UTF8.GetString(delta,nameOffset,Array.IndexOf(delta,(byte)0,nameOffset)-nameOffset)=="薄暮手记","selected new Steam name not present");
        using var records=JsonDocument.Parse(File.ReadAllBytes(ns));
        var groups=records.RootElement.EnumerateArray().ToList();
        var assigned=new List<int>();
        foreach(var entry in groups)
        {
            using var val=JsonDocument.Parse(entry[1].GetProperty("value").GetString()!);
            var ids=val.RootElement.GetProperty("added").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
            if(entry[0].GetString()=="user-collections.personal")
                Assert(ids.SequenceEqual(new[]{646570}),"personal collection changed");
            else assigned.AddRange(ids);
        }
        Assert(groups.Count==2 && assigned.SequenceEqual(new[]{9001105}),"unexpected managed memberships: "+string.Join(",",assigned));
        Log("Pending filters, multiword search, Ctrl+F, empty search, preview cancel, visible row picks and independent per-field writes matched on disk.");
    }

    static El EditorFor(string game)
    {
        El? edit=null;
        Until(()=>{
            edit=Find(main,El.AutomationIdProperty,"InPageHost");
            return edit!=null && !edit.Current.IsOffscreen && edit.Current.Name=="编辑游戏" &&
                Find(edit,El.AutomationIdProperty,"NameInput")!=null && TreeText(edit).Contains(game);
        },"editor for "+game);
        Window("编辑游戏");
        return edit!;
    }
    static void SaveEdit(El editor)
    {
        Click(Id(editor,"EditSave"));
        Until(()=>main.Current.IsEnabled && Ready() && Id(main,"StatusText").Current.Name.Contains("已保存"),"saved edit back in library");
    }
    static void TestReview(string root,string baseDir,TcpListener proxy)
    {
        var before=FilesIn(root);
        Navigate("待确认",4);
        var grid=Id(main,"LibraryGrid");
        Toggle(Find(grid,El.NameProperty,"勾选 Birch Archive")!,true);
        foreach(var (radio,total) in new[]{("ReviewMissingName",2),("ReviewMissingGroup",2),("ReviewMachine",1)})
        {
            var option=Id(main,radio);
            Assert((option.Current.Name+" "+TreeText(option)).Contains(total.ToString()),"incorrect reason count on "+radio);
        }
        Assert(!grid.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.HeaderItem))
            .Cast<El>().Any(e=>e.Current.Name=="状态" || e.Current.Name=="操作"),"old status or action column still visible");
        Screenshot(main,Path.Combine(baseDir,"review.png"));
        Click(Id(main,"PreviewButton"));
        var preview=Window("预览更改");
        Assert(VisibleMachineSources(preview)==1,"queue preview should identify exactly one machine translation");
        Click(Button(preview,"返回"));
        Until(()=>main.Current.IsEnabled && Id(main,"Search").Current.IsEnabled,"return from checking review source labels");

        Search("Cinder");Until(()=>Rows()==1,"search review target");
        PointerClick(Id(main,"TranslateButton"));
        Until(()=>proxy.Pending() && Id(main,"StopButton").Current.IsEnabled,"one visible local-proxy lookup");
        Click(Id(main,"StopButton"));
        Until(()=>Ready() && Id(main,"StatusText").Current.Name.Contains("已停止"),"contextual lookup cancellation");
        Click(Id(main,"ClearSearchButton"));Until(()=>Rows()==4,"return to four review games");

        Click(Id(grid,"RowEdit_9001101"));
        var amber=EditorFor("Amber Archive");
        Screenshot(amber,Path.Combine(baseDir,"review-edit.png"));
        ((ValuePattern)Id(amber,"NameInput").GetCurrentPattern(ValuePattern.Pattern)).SetValue("琥珀手记");
        Click(Id(amber,"EditContinue"));
        var birch=EditorFor("Birch Archive");
        Click(Id(birch,"SkipGroup"));
        Click(Id(birch,"EditContinue"));
        var cinder=EditorFor("Cinder Archive");
        Click(Button(cinder,"取消"));
        Until(()=>main.Current.IsEnabled && Rows()==3,"remaining review items after canceling third editor");
        var rest=TreeText(grid);
        Assert(rest.Contains("琥珀手记") && rest.Contains("Amber Archive") && rest.Contains("待选择") &&
            rest.Contains("Cinder Archive") && !rest.Contains("Birch Archive"),"partial edit lost unresolved reason or canceled next item");

        Search("Flint");Until(()=>Rows()==1,"machine translation review");
        Click(Id(grid,"RowEdit_9001106"));var machine=EditorFor("Flint Field Notes");
        Assert(((TogglePattern)Id(machine,"PinName").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState==ToggleState.Off,
            "machine name implicitly confirmed");
        SaveEdit(machine);
        Assert(Rows()==1 && TreeText(grid).Contains("核对机翻"),"saving unchanged machine name removed review");
        Click(Id(grid,"RowEdit_9001106"));machine=EditorFor("Flint Field Notes");
        Toggle(Id(machine,"PinName"),true);SaveEdit(machine);
        Until(()=>Rows()==0,"explicit confirmation removes machine review");
        Click(Id(main,"ClearSearchButton"));Until(()=>Rows()==2,"preserve review queue");
        Click(Id(grid,"RowEdit_9001101"));amber=EditorFor("Amber Archive");
        Click(Id(amber,"SkipGroup"));Click(Id(amber,"EditContinue"));
        cinder=EditorFor("Cinder Archive");
        Click(Id(cinder,"SkipName"));SaveEdit(cinder);
        Until(()=>Rows()==0 && Id(main,"EmptyTitle").Current.Name=="没有待确认的游戏","empty completed review queue");
        Assert(!Id(main,"PreviewButton").Current.IsEnabled,"completed review view permits hidden writes");
        Assert(before.SequenceEqual(FilesIn(root)),"review-only edits wrote to Steam");
        Click(Id(main,"EmptyButton"));
        Until(()=>IsPage("待应用") && Rows()>0,"review complete action goes to pending changes");
        Log("Review reasons, checked contextual lookup, save-and-next, partially resolved records, explicit machine approval, field skips and a completed queue matched the intended workflow.");
    }

    static bool On(El box) => ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState==ToggleState.On;
    static void AssertProgress()
    {
        Until(()=>{
            var bar=Find(main,El.AutomationIdProperty,"TaskProgress");
            if(bar==null || bar.Current.IsOffscreen || bar.Current.ControlType!=ControlType.ProgressBar ||
                !bar.TryGetCurrentPattern(RangeValuePattern.Pattern,out var pattern))return false;
            var range=((RangeValuePattern)pattern).Current;
            return range.Maximum>range.Minimum && range.Value>=range.Minimum && range.Value<=range.Maximum;
        },"online task reports an accessible progress range");
    }
    static int NavCount(string label)
    {
        var element=Find(Id(main,"Navigation"),El.NameProperty,label)!;
        while(element.Current.ControlType!=ControlType.ListItem)element=TreeWalker.ControlViewWalker.GetParent(element);
        return element.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.Text))
            .Cast<El>().Select(e=>e.Current.Name).Where(s=>int.TryParse(s,out _)).Select(int.Parse).Single();
    }
    static void ChooseGroup(string group)
    {
        ShowFilters();
        var box=Id(main,"GroupFilter");
        PointerClick(box);
        El? option=null;
        Until(()=>{
            option=box.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.ListItem))
                .Cast<El>().FirstOrDefault(e=>e.Current.Name==group && !e.Current.IsOffscreen);
            return option!=null;
        },"open suggested collection '"+group+"'");
        PointerClick(option!);
        Until(()=>((ExpandCollapsePattern)box.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState==ExpandCollapseState.Collapsed,"dismiss collection picker");
    }
    static int[] ManagedAssignments(string ns)
    {
        using var file=JsonDocument.Parse(File.ReadAllBytes(ns));
        var list=new List<int>();
        foreach(var entry in file.RootElement.EnumerateArray())
        {
            if(entry[0].GetString()=="user-collections.personal")continue;
            using var content=JsonDocument.Parse(entry[1].GetProperty("value").GetString()!);
            list.AddRange(content.RootElement.GetProperty("added").EnumerateArray().Select(item=>item.GetInt32()));
        }
        return list.Order().ToArray();
    }
    static void FreshFacets(string root,string baseDir)
    {
        int start=pointerActivations;
        ShowFilters();
        PointerClick(Id(main,"InstalledFilter"));
        Until(()=>Rows()==3,"three installed fixture games");
        ChooseGroup("策略与经营");
        Until(()=>Rows()==2 && NavCount("游戏库")==2 && NavCount("待确认")==1 && NavCount("待应用")==2,
            "installed strategy games and scope counts");
        Assert(pointerActivations-start==4,"reveal filters + checkbox + open picker + select group");
        Log("Opening filters and selecting installed + collection took four pointer activations for two installed strategy games.");
        PointerClick(Id(main,"FilterButton"));
        Until(()=>!IsVisible(main,El.AutomationIdProperty,"InstalledFilter") && Id(main,"FilterButton").Current.Name.Contains("仅已安装") &&
            Id(main,"FilterButton").Current.Name.Contains("策略与经营"),"collapsed filters identify both active conditions");
        Assert(Rows()==2 && IsVisible(main,El.AutomationIdProperty,"ClearFiltersButton"),"collapsed filters changed scope or hid reset");
        Screenshot(main,Path.Combine(baseDir,"faceted.png"));
        Search("cinder");Until(()=>Rows()==1,"search inside both facets");
        Navigate("待确认",1);Navigate("待应用",1);
        Assert(Id(main,"FilterButton").Current.Name.Contains("仅已安装") && TreeText(Id(main,"LibraryGrid")).Contains("Cinder Archive"),"workspace navigation discarded facets");
        start=pointerActivations;
        PointerClick(Id(main,"ClearFiltersButton"));
        Until(()=>!Id(main,"FilterButton").Current.Name.Contains("仅已安装") && Rows()==1 && IsPage("待应用"),
            "clear facets preserves search and workspace");
        Assert(((ValuePattern)Id(main,"Search").GetCurrentPattern(ValuePattern.Pattern)).Current.Value=="cinder","reset unexpectedly erased search");
        Assert(pointerActivations-start==1,"reset facets count");
        Click(Id(main,"ClearSearchButton"));Until(()=>Rows()==6,"clear remaining search");
        Navigate("游戏库",8);

        string cache=Path.Combine(root,"appcache","appinfo.vdf");
        var initial=V29Records(cache);
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var acf=Path.Combine(root,"steamapps","appmanifest_646570.acf");
        var snapshot=FilesIn(root);
        ShowFilters();PointerClick(Id(main,"InstalledFilter"));ChooseGroup("策略与经营");
        Search("spire");Until(()=>Rows()==1 && TreeText(Id(main,"LibraryGrid")).Contains("Slay the Spire"),"compound scope for trusted write");
        start=pointerActivations;
        PointerClick(Id(main,"PreviewButton"));
        var preview=Window("预览更改");
        var previewGrid=Find(preview,El.ControlTypeProperty,ControlType.DataGrid)!;
        Assert(Count(previewGrid)==1 && On(Id(preview,"PreviewName_646570")) && On(Id(preview,"PreviewGroup_646570")),"trusted defaults in selected scope");
        PointerClick(Id(preview,"PreviewApply"));
        Until(()=>Sha(acf)!=snapshot[Path.GetRelativePath(root,acf)] && Ready(),"trusted two-click application");
        Assert(pointerActivations-start==2,"ordinary trusted application activation count");
        var after=V29Records(cache);
        Assert(initial.Where(x=>!after[x.Key].SequenceEqual(x.Value)).Select(x=>x.Key).SequenceEqual(new[]{646570}),"hidden game was renamed");
        Assert(ManagedAssignments(ns).SequenceEqual(new[]{646570}),"hidden game received collection");
        foreach(var item in snapshot.Where(x=>x.Key.EndsWith(".acf") && x.Key!=Path.GetRelativePath(root,acf)))
            Assert(Sha(Path.Combine(root,item.Key))==item.Value,"hidden game manifest modified");
        Log("One trusted game in a search + installed + collection intersection: 2 pointer activations from filtered results to verified Steam writes. No hidden records changed.");
        Click(Id(main,"ClearSearchButton"));
        Click(Id(main,"ClearFiltersButton"));Until(()=>Rows()==8,"restore unfiltered library");
    }
    static void MachineSafeguards(string root,string baseDir,string prefs)
    {
        string cache=Path.Combine(root,"appcache","appinfo.vdf"),original=Sha(cache);
        string ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        ChooseGroup("角色扮演");Until(()=>Rows()==2,"both trusted and machine-suggested records");
        Click(Id(main,"PreviewButton"));var preview=Window("预览更改");
        var deferred=Id(preview,"PreviewName_9001106");
        Assert(!On(deferred) && On(Id(preview,"PreviewGroup_9001106")) && On(Id(preview,"PreviewName_9001105")),
            "machine name must be opt-in; trusted name and ordinary collections must start selected");
        var names=(TogglePattern)Id(preview,"PreviewAllNames").GetCurrentPattern(TogglePattern.Pattern);
        Assert(names.Current.ToggleState==ToggleState.Indeterminate,"safe/unsafe name mix should expose a mixed column header");
        names.Toggle();
        Until(()=>!On(Id(preview,"PreviewName_9001105")) && On(Id(preview,"PreviewGroup_9001105")) &&
            On(Id(preview,"PreviewGroup_9001106")),"clearing all names leaves selected memberships alone");
        names.Toggle();
        Until(()=>names.Current.ToggleState==ToggleState.Indeterminate && !On(deferred) &&
            On(Id(preview,"PreviewName_9001105")),"restoring the name column only selects trusted names");
        Screenshot(preview,Path.Combine(baseDir,"preview-machine.png"));
        var all=Id(preview,"PreviewAll");
        ((TogglePattern)all.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(()=>!Id(preview,"PreviewApply").Current.IsEnabled,"clearing bulk defaults");
        ((TogglePattern)all.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(()=>On(Id(preview,"PreviewGroup_9001106")) && !On(deferred) && On(Id(preview,"PreviewName_9001105")),"bulk restore excludes machine name");
        var group=Id(preview,"PreviewGroup_9001106");
        Toggle(group,false);
        var game=Id(preview,"PreviewGame_9001106");
        ((TogglePattern)game.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(()=>On(group) && !On(deferred),"per-game inclusion skips unapproved name");
        Click(Button(preview,"返回"));Until(()=>Id(main,"ClearFiltersButton").Current.IsEnabled,"back from safe preview");
        Click(Id(main,"ClearFiltersButton"));Until(()=>Rows()==8,"reset mixed preview scope");
        Search("Flint");Until(()=>Rows()==1,"select only machine name and ordinary group");
        Click(Id(main,"PreviewButton"));preview=Window("预览更改");
        Assert(!On(Id(preview,"PreviewName_9001106")) && On(Id(preview,"PreviewGroup_9001106")),"defaults survive canceled preview");
        Assert(!Id(preview,"PreviewAllNames").Current.IsEnabled && Id(preview,"PreviewAllGroups").Current.IsEnabled,
            "a column containing only unapproved machine names must not enable bulk opt-in");
        Click(Id(preview,"PreviewApply"));
        Until(()=>ManagedAssignments(ns).SequenceEqual(new[]{646570,9001106}) && Ready(),"write only the category");
        Assert(Sha(cache)==original,"unconfirmed machine name was written");
        Navigate("待应用",0);Navigate("待确认",1);
        Click(Id(main,"PreviewButton"));preview=Window("预览更改");
        Assert(!Id(preview,"PreviewApply").Current.IsEnabled && !Id(preview,"PreviewAll").Current.IsEnabled &&
            !Id(preview,"PreviewGame_9001106").Current.IsEnabled && !On(Id(preview,"PreviewName_9001106")),"pure unapproved machine translation should have no preselected or bulk actions");
        Toggle(Id(preview,"PreviewName_9001106"),true);
        Assert(Id(preview,"PreviewApply").Current.IsEnabled,"individual opt-in should permit one pending translation");
        Click(Button(preview,"返回"));Until(Ready,"return without writing opted-in field");
        Assert(Sha(cache)==original,"preview cancellation wrote machine name");
        Click(Id(main,"RowEdit_9001106"));var edit=EditorFor("Flint Field Notes");
        int start=pointerActivations;
        PointerClick(Id(edit,"AcceptMachineName"));
        Until(()=>main.Current.IsEnabled && Rows()==0 && Ready(),"direct adoption clears only outstanding review");
        using var saved=JsonDocument.Parse(File.ReadAllBytes(prefs));
        Assert(saved.RootElement.GetProperty("9001106").GetProperty("ManualName").GetString()=="燧石手记" && Sha(cache)==original,
            "one-click adoption didn't save the exact choice or wrote Steam before application");
        Assert(pointerActivations-start==1,"direct translation adoption count");
        Navigate("待应用",1);
        Click(Id(main,"PreviewButton"));preview=Window("预览更改");
        Assert(On(Id(preview,"PreviewName_9001106")),"approved name is an ordinary checked write");
        Click(Id(preview,"PreviewApply"));Until(()=>Sha(cache)!=original && Ready(),"explicitly adopted name applied");
        Log("Unapproved machine translation stayed excluded from default, row and whole-list selections. Only its category was written. One press in the editor approved the exact name; it was written only after preview and apply.");

        Navigate("游戏库",1);
        Search("Frost");Until(()=>Rows()==1 && TreeText(Id(main,"LibraryGrid")).Contains("Frost Notebook"),"second unconfirmed name");
        Click(Id(main,"PreviewButton"));preview=Window("预览更改");
        var explicitName=Id(preview,"PreviewName_9001107");
        Assert(!On(explicitName),"new machine name implicitly accepted");
        Toggle(explicitName,true);Click(Id(preview,"PreviewApply"));
        Until(()=>{
            using var latest=JsonDocument.Parse(File.ReadAllBytes(prefs));
            return latest.RootElement.GetProperty("9001107").TryGetProperty("ManualName",out var value) &&
                value.GetString()=="霜原手记" && Ready();
        },"successful individual preview acceptance recorded");
        Navigate("待确认",1);
        var review=TreeText(Id(main,"LibraryGrid"));
        Assert(review.Contains("霜原手记") && review.Contains("待选择") && !review.Contains("核对机翻"),"individually accepted name remained in machine review or resolved unrelated group");
        Log("Direct field opt-in during preview saved the applied machine name as confirmed without resolving the game's unrelated missing group.");
    }
    static void Facets(string root,string baseDir,string data)
    {
        FreshFacets(root,baseDir);
        MachineSafeguards(root,baseDir,Path.Combine(data,"suggestions.v2.json"));
    }

    static void FilterReview(string id,int expected)
    {
        var radio=Id(main,id);
        ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Until(()=>Rows()==expected && ((SelectionItemPattern)Id(main,id).GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
            id+" intersects active filters with "+expected+" rows");
    }
    static void ExpectPreviewFields(El preview,int names,int groups)
    {
        var summary=Id(preview,"PreviewSummary");
        Until(()=>summary.Current.Name.Contains($"{names} 个名称") && summary.Current.Name.Contains($"{groups} 项收藏集"),
            $"preview should show {names} names and {groups} memberships");
    }
    static HashSet<string> BackupFolders(string data)
    {
        string backups=Path.Combine(data,"backups");
        return Directory.Exists(backups)?Directory.EnumerateFiles(backups,"snapshot.json",SearchOption.AllDirectories)
            .Select(f=>Path.GetDirectoryName(f)!).ToHashSet():new HashSet<string>();
    }
    static void LaterFixtureSnapshot(string existing)
    {
        // Simulate an intervening snapshot from another operation without launching a second application.
        // Its copied manifest and original files remain valid for this same isolated account.
        var stamp=DateTime.Now.AddSeconds(1).ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture);
        var copy=Path.Combine(Path.GetDirectoryName(existing)!,stamp+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(copy);
        foreach(string file in Directory.EnumerateFiles(existing,"*",SearchOption.AllDirectories))
        {
            var dest=Path.Combine(copy,Path.GetRelativePath(existing,file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file,dest);
        }
    }
    static void Workspace(string root,string baseDir,string data)
    {
        var initial=FilesIn(root);
        string cache=Path.Combine(root,"appcache","appinfo.vdf");
        var original=V29Records(cache);
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        Assert(!IsVisible(main,El.AutomationIdProperty,"LastBackupButton"),"unopened session has no write to restore");
        Navigate("待确认",5);
        FilterReview("ReviewMissingName",2);
        var grid=Id(main,"LibraryGrid");
        Assert(TreeText(grid).Contains("Cinder Archive") && TreeText(grid).Contains("Amber Archive"),"wrong missing-name queue");
        FilterReview("ReviewMissingGroup",3);
        Assert(TreeText(grid).Contains("Birch Archive") && TreeText(grid).Contains("Frost Notebook"),"overlapping missing-group reason lost");
        FilterReview("ReviewMachine",2);
        Assert(TreeText(grid).Contains("Flint Field Notes") && TreeText(grid).Contains("Frost Notebook"),"wrong machine-review queue");
        Screenshot(main,Path.Combine(baseDir,"workspace-reasons.png"));
        ChooseGroup("角色扮演");
        Until(()=>Rows()==1 && TreeText(grid).Contains("Flint Field Notes"),"machine reason plus group");
        SetCheck("InstalledFilter",true);
        Until(()=>Rows()==0 && !Id(main,"PreviewButton").Current.IsEnabled,"empty installed RPG machine-review intersection");
        Search("archive");
        Click(Id(main,"ClearFiltersButton"));
        Until(()=>Rows()==3 && !On(Id(main,"InstalledFilter")) && ((SelectionItemPattern)Id(main,"ReviewAll")
            .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,"reset review reason, installation and group");
        Assert(((ValuePattern)Id(main,"Search").GetCurrentPattern(ValuePattern.Pattern)).Current.Value=="archive","reset erased the query");
        FilterReview("ReviewMissingGroup",2);
        Navigate("游戏库",5);Navigate("待确认",3);
        Assert(((SelectionItemPattern)Id(main,"ReviewAll").GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,"re-entering review retained previous reason");
        Click(Id(main,"ClearSearchButton"));Until(()=>Rows()==5,"unfiltered review scope");
        FilterReview("ReviewMissingName",2);

        ClickCell(grid,0);ClickCell(grid,1,0x11);
        Until(()=>On(Find(grid,El.NameProperty,"勾选 Amber Archive")!) && On(Find(grid,El.NameProperty,"勾选 Cinder Archive")!),"two selected review rows");
        PointerClick(Id(grid,"RowEdit_9001103"));
        var editor=EditorFor("Cinder Archive");
        ((ValuePattern)Id(editor,"NameInput").GetCurrentPattern(ValuePattern.Pattern)).SetValue("余烬手记");
        SaveEdit(editor);
        Until(()=>Rows()==1 && TreeText(grid).Contains("Amber Archive"),"only edited row left missing-name view");
        using(var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"suggestions.v2.json"))))
        {
            var manual=doc.RootElement.EnumerateObject().Where(p=>p.Value.TryGetProperty("ManualName",out var value) &&
                value.ValueKind==JsonValueKind.String).ToArray();
            Assert(manual.Length==1 && manual[0].Name=="9001103" && manual[0].Value.GetProperty("ManualName").GetString()=="余烬手记","row edit changed other highlighted/checked rows");
        }
        Assert(initial.SequenceEqual(FilesIn(root)),"editing suggestions prematurely wrote Steam");
        Navigate("游戏库",8);
        var summary=Id(main,"SelectionSummary").Current.Name;
        Assert(summary.Contains("8 / 8"),"row action changed the picked games: "+summary);
        Search("archive");Until(()=>Rows()==5,"five matching titles");
        Toggle(Find(grid,El.NameProperty,"勾选 Dusk Archive")!,false);
        Click(Id(main,"PreviewButton"));var preview=Window("预览更改");
        ExpectPreviewFields(preview,3,2);
        Toggle(Id(preview,"PreviewAllNames"),false);ExpectPreviewFields(preview,0,2);
        Toggle(Id(preview,"PreviewAllGroups"),false);ExpectPreviewFields(preview,0,0);
        Assert(!Id(preview,"PreviewApply").Current.IsEnabled,"zero-field preview still enables apply");
        Toggle(Id(preview,"PreviewAllNames"),true);ExpectPreviewFields(preview,3,0);
        Toggle(Id(preview,"PreviewAllGroups"),true);ExpectPreviewFields(preview,3,2);
        var counts=Id(preview,"PreviewSummary").Current.Name;
        Assert(counts.Contains("3 款") && counts.Contains("3 个名称") && counts.Contains("2 项收藏集"),"preview differs from pre-preview field count: "+counts);
        Screenshot(preview,Path.Combine(baseDir,"workspace-preview.png"));
        Click(Id(preview,"PreviewApply"));
        Until(()=>BackupFolders(data).Count==1 && IsVisible(main,El.AutomationIdProperty,"LastBackupButton") &&
            Id(main,"LastBackupButton").Current.IsEnabled && Ready(),"completed write reveals exact snapshot shortcut");
        var changed=V29Records(cache).Where(x=>!x.Value.SequenceEqual(original[x.Key])).Select(x=>x.Key).ToHashSet();
        Assert(changed.SetEquals(new[]{9001102,9001103,9001105}) && ManagedAssignments(ns).SequenceEqual(new[]{9001103,9001105}),
            "disk writes differ from checked field counts or include hidden rows");
        Assert(!Id(main,"PreviewButton").Current.IsEnabled,"completed selection still has writable fields");
        string own=BackupFolders(data).Single();
        LaterFixtureSnapshot(own);
        var beforeCancel=FilesIn(root);
        Click(Id(main,"LastBackupButton"));var dialog=Window("备份与还原");
        var list=Id(dialog,"BackupList");
        var selected=((SelectionPattern)list.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().Single();
        var items=list.FindAll(TreeScope.Children,new PropertyCondition(El.ControlTypeProperty,ControlType.ListItem)).Cast<El>().ToList();
        var stamp=DateTime.ParseExact(Path.GetFileName(own)[..19],"yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture);
        var selectedText=selected.Current.Name+" "+TreeText(selected);
        Assert(items.Count==2 && !selected.Equals(items[0]) && selectedText.Contains("本次操作") &&
            selectedText.Contains(stamp.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture)),
            "shortcut selected a later snapshot from another window: "+selected.Current.Name);
        Screenshot(dialog,Path.Combine(baseDir,"workspace-snapshot.png"));
        Click(Button(dialog,"还原所选备份"));
        var confirmation=Window("确认还原");
        Assert(TreeText(confirmation).Contains("覆盖") && Id(confirmation,"ConfirmRecovery").Current.IsEnabled,"snapshot confirmation does not name its impact");
        Click(Button(confirmation,"返回"));dialog=Window("备份与还原");
        Click(Button(dialog,"关闭"));
        Until(()=>main.Current.IsEnabled && Ready(),"cancel exact snapshot shortcut");
        Assert(beforeCancel.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==2,"viewing or canceling the snapshot altered Steam");
        Log("Review filters intersected search, group and installation. Inline editing changed only the clicked game despite a multi-selection. Preview field counts and disk agreed on three names and two assignments. The shortcut preselected its own actual snapshot even after introducing a later valid fixture copy.");
    }
    static void Workbench(string root,string baseDir,TcpListener proxy)
    {
        TestPending(root,baseDir);
        Search("");
        TestReview(root,baseDir,proxy);
    }

    static void WithFixtureSteam(string root,Action<Process> test)
    {
        // This child is the only simulated Steam process controlled during test teardown.
        var markerPath=Path.Combine(root,"steam.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory,"ping.exe"),markerPath);
        try
        {
            using var marker=Process.Start(new ProcessStartInfo(markerPath,"-t 127.0.0.1"){
                UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true})!;
            try
            {
                Until(()=>{
                    marker.Refresh();
                    if(marker.HasExited)throw new Exception($"isolated Steam marker exited with code {marker.ExitCode}");
                    return string.Equals(marker.MainModule?.FileName,markerPath,StringComparison.OrdinalIgnoreCase);
                },"isolated Steam marker process initialized");
                test(marker);
            }
            finally {if(!marker.HasExited){marker.Kill();marker.WaitForExit();}}
        }
        finally {File.Delete(markerPath);}
    }
    static void DynamicCollection(string root,string baseDir,string data,Process first,string exe)
    {
        var initial=FilesIn(root);
        var cloud=Path.Combine(root,"userdata","1","config","cloudstorage");
        var ns=Path.Combine(cloud,"cloud-storage-namespace-1.json");
        var versions=Path.Combine(cloud,"cloud-storage-namespaces.json");
        using var prior=JsonDocument.Parse(File.ReadAllBytes(ns));
        var old=prior.RootElement.EnumerateArray().ToDictionary(e=>e[0].GetString()!,e=>e[1]);
        int Snapshots() => Directory.Exists(Path.Combine(data,"backups")) ?
            Directory.EnumerateFiles(Path.Combine(data,"backups"),"snapshot.json",SearchOption.AllDirectories).Count() : 0;
        Toggle(Find(Id(main,"LibraryGrid"),El.NameProperty,"勾选当前列表")!,false);
        Navigate("待确认",1);
        SetCheck("InstalledFilter",true);
        Search("no-such-game");Until(()=>Rows()==0,"empty, unselected compound scope");
        var button=Id(main,"CreateAllGamesButton");
        Assert(button.Current.IsEnabled && !Id(main,"PreviewButton").Current.IsEnabled,"account collection requires a visible/checked game");

        WithFixtureSteam(root,marker=>{
            var markerFiles=FilesIn(root);
            Click(button);
            var confirmation=Window("创建「全部游戏」动态收藏");
            Assert(Button(confirmation,"退出 Steam 并创建").Current.IsEnabled,"missing explicit shutdown approval");
            Click(Button(confirmation,"稍后再说"));
            Until(()=>Id(main,"CreateAllGamesButton").Current.IsEnabled && Id(main,"StatusText").Current.Name.Contains("已取消"),"cancel before shutdown");
            Assert(!marker.HasExited && Snapshots()==0 && markerFiles.SequenceEqual(FilesIn(root)),"cancel stopped Steam, changed files or created backup");
        });
        int activations=pointerActivations;
        PointerClick(Id(main,"CreateAllGamesButton"));
        Until(()=>Ready() && !Id(main,"CreateAllGamesButton").Current.IsEnabled &&
            Id(main,"CreateAllGamesButton").Current.Name.Contains("全部游戏") && Snapshots()==1,
            "one-click dynamic collection");
        Assert(pointerActivations==activations+1 && Snapshots()==1,"collection create cost or snapshot count");
        var after=FilesIn(root);
        var touched=after.Where(p=>!initial.TryGetValue(p.Key,out var v)||v!=p.Value).Select(p=>p.Key).ToHashSet();
        Assert(initial.Keys.SequenceEqual(after.Keys) && touched.SetEquals(new[]{Path.GetRelativePath(root,ns),Path.GetRelativePath(root,versions)}),
            "dynamic-only creation changed something outside account 1's collection and namespace version");
        using var current=JsonDocument.Parse(File.ReadAllBytes(ns));
        var entries=current.RootElement.EnumerateArray().ToDictionary(e=>e[0].GetString()!,e=>e[1]);
        Assert(entries.Count==old.Count+1 && old.All(p=>entries.TryGetValue(p.Key,out var same)&&JsonElement.DeepEquals(same,p.Value)),"an unrelated user record changed");
        var created=entries.Single(p=>!old.ContainsKey(p.Key));
        using var content=JsonDocument.Parse(created.Value.GetProperty("value").GetString()!);
        var item=content.RootElement;
        Assert(created.Key=="user-collections."+item.GetProperty("id").GetString() && created.Value.GetProperty("key").GetString()==created.Key &&
            item.GetProperty("name").GetString()=="全部游戏" && item.GetProperty("added").GetArrayLength()==0 && item.GetProperty("removed").GetArrayLength()==0,
            "collection was static, included explicit apps or had the wrong key/name");
        var spec=item.GetProperty("filterSpec");
        var groups=spec.GetProperty("filterGroups").EnumerateArray().ToArray();
        Assert(spec.GetProperty("nFormatVersion").GetInt32()==2 && spec.GetProperty("strSearchText").GetString()=="" &&
            !spec.GetProperty("setSuggestions").EnumerateObject().Any() && groups.Length==9 &&
            groups[1].GetProperty("bAcceptUnion").GetBoolean() && groups[1].GetProperty("rgOptions").EnumerateArray().Select(x=>x.GetInt32()).Order().SequenceEqual(new[]{3,4}) &&
            groups.Where((_,i)=>i!=1).All(g=>g.GetProperty("rgOptions").GetArrayLength()==0),"expected played OR never-played in native v2 filter");
        var newVersion=long.Parse(created.Value.GetProperty("version").GetString()!);
        using var revision=JsonDocument.Parse(File.ReadAllBytes(versions));
        Assert(revision.RootElement.EnumerateArray().Any(v=>v[0].GetInt32()==1 && long.Parse(v[1].GetString()!)==newVersion) &&
            revision.RootElement.EnumerateArray().Any(v=>v[0].GetInt32()==6 && v[1].GetString()=="9") && newVersion>10,"namespace bookkeeping changed other revisions");
        File.Copy(ns,Path.Combine(baseDir,"dynamic-cloud-records.json"));
        Log("Created a native v2 played OR never-played rule in one real pointer activation from an empty unchecked view, with zero enumerated AppIDs. Only the two cloud files for the selected fixture account changed. Canceling with a simulated running client left it running.");
        SetCheck("InstalledFilter",false);Click(Id(main,"ClearSearchButton"));Navigate("游戏库",12);
        Screenshot(main,Path.Combine(baseDir,"dynamic-created.png"));

        Assert(first.CloseMainWindow() && first.WaitForExit(3000),"cleanly close fixture application for relaunch");
        var prefs=Path.Combine(data,"desktop.json");
        File.WriteAllText(prefs,JsonSerializer.Serialize(new {Root=root,Account="1",AllCached=false,RestartSteam=false,AutoLookup=false}));
        var userConfig=Path.Combine(root,"userdata","1","config","localconfig.vdf");
        var local=File.ReadAllText(userConfig);
        File.WriteAllText(userConfig,local.Replace("\"646570\" { }","\"646570\" { } \"504230\" { }"));
        using var second=Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe)!,UseShellExecute=false})!;
        try
        {
            pid=second.Id;main=Window("Steam 统译");ActivateFixture();
            Until(()=>Rows()==2 && Ready() && !Id(main,"CreateAllGamesButton").Current.IsEnabled &&
                Id(main,"CreateAllGamesButton").Current.Name.Contains("全部游戏"),
                "recognize collection after new ownership and restart");
            Assert(!Id(main,"CreateAllGamesButton").Current.IsEnabled && TreeText(Id(main,"LibraryGrid")).Contains("Celeste"),"already-created status or new ownership missing");
            MoreAction("ScanButton");Until(()=>Ready() && Rows()==2,"repeat scan after restart");
            Assert(Sha(ns)==after[Path.GetRelativePath(root,ns)] && Sha(versions)==after[Path.GetRelativePath(root,versions)] && Snapshots()==1,
                "reopening/re-scanning regenerated the rule, created a duplicate or wrote an unnecessary backup");
            Restore();
            Until(()=>Sha(ns)==initial[Path.GetRelativePath(root,ns)] && Sha(versions)==initial[Path.GetRelativePath(root,versions)] &&
                Id(main,"CreateAllGamesButton").Current.IsEnabled,"snapshot restore removes only the created collection and enables creation");
            Log("Reopened with a newly owned fixture game. The same rule remained byte-identical with one snapshot. Restoring the snapshot recovered the two previous account files exactly.");
        }
        finally
        {
            if(!second.HasExited && (!second.CloseMainWindow() || !second.WaitForExit(2000)))second.Kill();
        }
    }


    static void DirectActions(string root,string baseDir,string data,TcpListener proxy)
    {
        var grid=Id(main,"LibraryGrid");
        var cache=Path.Combine(root,"appcache","appinfo.vdf");
        var cloud=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var site=FilesIn(root);
        Assert(!proxy.Pending(),"the initial local scan contacted the network");
        Assert(!IsVisible(main,El.AutomationIdProperty,"InstalledFilter") && !TreeText(grid).Contains("Unowned Cache Entry"),"an unowned cache entry is visible by default");
        Assert(Id(main,"ClassifyButton").Current.Name.Contains("一键分类") &&
            Id(main,"TranslateButton").Current.Name.Contains("一键翻译"),"primary actions are not labeled on the first screen");
        Screenshot(main,Path.Combine(baseDir,"small-library.png"));
        NarrowScreenshot(baseDir,"small-narrow.png",new[]{"ClassifyButton","TranslateButton","BatchSelectButton","PreviewButton","Search","FilterButton"});
        Search("9001199");Until(()=>Rows()==0,"a cache-only, unowned title is absent in default scope");
        Assert(!Id(main,"ClassifyButton").Current.IsEnabled && !Id(main,"TranslateButton").Current.IsEnabled,
            "an empty visible list permits direct writes");
        Search("646570");Until(()=>Rows()==1 && Id(main,"ClassifyButton").Current.IsEnabled,"single-game library result");
        Screenshot(main,Path.Combine(baseDir,"one-game.png"));

        Search("archive");Until(()=>Rows()==5,"five visible archive games");
        Toggle(Find(grid,El.NameProperty,"勾选 Dusk Archive")!,false);
        Click(Id(main,"PreviewButton"));var preview=Window("预览更改");
        Assert(Find(preview,El.AutomationIdProperty,"PreviewGame_9001104")==null &&
            Find(preview,El.AutomationIdProperty,"PreviewGame_646570")==null,"preview includes unchecked or hidden games");
        Click(Button(preview,"返回"));
        Until(()=>Id(main,"ClassifyButton").Current.IsEnabled,"direct classification after preview cancellation");

        Assert(site.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==0,"opening and closing details wrote files");
        WithFixtureSteam(root,marker=>{
            var before=FilesIn(root);
            PointerClick(Id(main,"ClassifyButton"));
            var prompt=Window("退出 Steam 并整理");
            Click(Button(prompt,"稍后再说"));
            Until(()=>Ready(),"cancel direct classification");
            Assert(!marker.HasExited && before.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==0,
                "canceling classification closed the fixture client or wrote files");
        });
        var clicks=pointerActivations;
        PointerClick(Id(main,"ClassifyButton"));
        Until(()=>BackupFolders(data).Count==1 && ManagedAssignments(cloud).SequenceEqual(new[]{9001103,9001105}) &&
            Ready(),"classification writes the visible checked Cinder and Elm memberships");
        var changed=FilesIn(root).Where(kv=>kv.Value!=site[kv.Key]).Select(kv=>Path.GetFileName(kv.Key)).Order().ToArray();
        Assert(clicks+1==pointerActivations && changed.SequenceEqual(new[]{"cloud-storage-namespace-1.json","cloud-storage-namespaces.json"}) &&
            !proxy.Pending(),"classification changed names, contacted the network or took more than one click");
        var groupsHash=Sha(cloud);
        Click(Id(main,"LastBackupButton"));var backups=Window("备份与还原");
        Assert(TreeText(Id(backups,"BackupList")).Contains("本次操作"),"one-click classification does not identify its snapshot");
        Click(Button(backups,"关闭"));

        Search("birch");Until(()=>Rows()==1 && Id(main,"TranslateButton").Current.IsEnabled,"one trusted uninstalled title");
        File.Copy(Path.Combine(baseDir,"direct-updated.vdf"),cache,true);
        var baseline=FilesIn(root);
        var records=V29Records(cache);
        PointerClick(Id(main,"TranslateButton"));
        Until(()=>Id(main,"StatusText").Current.Name.Contains("已更新") && Id(main,"TranslateButton").Current.IsEnabled,
            "a changed official name is refreshed before writing");
        Assert(baseline.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==1 && !proxy.Pending(),
            "stale translation wrote data, contacted a server or created a snapshot");
        clicks=pointerActivations;PointerClick(Id(main,"TranslateButton"));
        Until(()=>BackupFolders(data).Count==2 && Ready() && Sha(cache)!=baseline[Path.GetRelativePath(root,cache)],
            "apply fresh trusted name");
        var after=V29Records(cache);
        var renamed=records.Where(kv=>!after[kv.Key].SequenceEqual(kv.Value)).Select(kv=>kv.Key).ToArray();
        Assert(clicks+1==pointerActivations && renamed.SequenceEqual(new[]{9001102}) && Encoding.UTF8.GetString(after[9001102]).Contains("白桦新篇") &&
            Sha(cloud)==groupsHash && !proxy.Pending(),"direct trusted translation changed other games/collections or accessed the network");

        // Refuse all outbound CONNECT tunnels locally. Failures exercise the ordinary HTTP path without sending data to any service.
        Search("");Until(()=>Rows()==8 && Id(main,"TranslateButton").Current.IsEnabled,"mixed local, missing and unconfirmed machine names");
        Toggle(Find(grid,El.NameProperty,"勾选 Slay the Spire")!,false);
        int attempts=0,machineRequested=0;
        using var offline=new CancellationTokenSource();
        var refusal=System.Threading.Tasks.Task.Run(async()=>{
            try
            {
                while(!offline.IsCancellationRequested)
                {
                    using var client=await proxy.AcceptTcpClientAsync(offline.Token);
                    using var stream=client.GetStream();
                    using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
                    var request=await reader.ReadLineAsync(offline.Token);
                    while(await reader.ReadLineAsync(offline.Token) is { Length: > 0 }) {}
                    Interlocked.Increment(ref attempts);
                    if(request?.Contains("api.mymemory.translated.net",StringComparison.OrdinalIgnoreCase)==true &&
                        Interlocked.Increment(ref machineRequested)==2)
                        await System.Threading.Tasks.Task.Delay(Timeout.Infinite,offline.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),offline.Token);
                }
            }
            catch(OperationCanceledException) when(offline.IsCancellationRequested){}
        });
        try
        {
            clicks=pointerActivations;PointerClick(Id(main,"TranslateButton"));
            Until(()=>Volatile.Read(ref attempts)>0 && BackupFolders(data).Count==3 && Ready() &&
                !V29Records(cache)[9001105].SequenceEqual(after[9001105]),"offline lookup preserves known trusted translations",35);
            var final=V29Records(cache);
            Assert(clicks+1==pointerActivations && after.Where(kv=>!final[kv.Key].SequenceEqual(kv.Value)).Select(kv=>kv.Key).SequenceEqual(new[]{9001105}) &&
                Sha(cloud)==groupsHash && File.ReadAllText(Path.Combine(root,"steamapps","appmanifest_9001104.acf")).Contains("Dusk Archive"),
                "offline translation modified unchecked games, missing names or membership");
            Until(()=>{
                if(Volatile.Read(ref machineRequested)!=2 || Id(main,"StopButton").Current.Name!="停止补译" ||
                    !Id(main,"CandidateStatus").Current.Name.Contains("1 / 2"))return false;
                var range=((RangeValuePattern)Id(main,"TaskProgress").GetCurrentPattern(RangeValuePattern.Pattern)).Current;
                return range.Minimum==0 && range.Value==1 && range.Maximum==2;
            },"one completed machine lookup and one pending lookup produce a true half-full progress bar");
            Search("frost");Until(()=>Rows()==1 && TreeText(grid).Contains("Frost Notebook"),"library stays responsive during a stalled machine request");
            Search("");Until(()=>Rows()==8,"restore the library while the remaining background request is still pending");
            Screenshot(main,Path.Combine(baseDir,"background-translation.png"));
            var beforeStop=FilesIn(root);
            Click(Id(main,"StopButton"));
            Until(()=>!IsVisible(main,El.AutomationIdProperty,"StopButton") && !IsVisible(main,El.AutomationIdProperty,"TaskProgress"),"background candidate request canceled and progress hidden");
            Assert(beforeStop.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==3,"stopping machine candidates changed Steam");
        }
        finally {offline.Cancel();refusal.GetAwaiter().GetResult();}
        Search("frost");Until(()=>Rows()==1,"cached machine-only title");
        Assert(!Id(main,"TranslateButton").Current.IsEnabled,"an unconfirmed machine-only name can be written by direct translation");
        Assert(V29Records(cache)[9001107].SequenceEqual(records[9001107]),"unsafe machine name was written");
        Search("");Until(()=>Rows()==8,"return to the small library");
        Screenshot(main,Path.Combine(baseDir,"direct-complete.png"));
        Log("Scoped classification and translation each used a single pointer press. Checked, unchecked, uninstalled, filtered-out, missing, machine-only and unowned entries were checked on disk. Stale/quit cancellation produced no writes; local proxy failures preserved available trusted translations.");
    }

    static void Selection(string root,string data,string baseDir)
    {
        var before=FilesIn(root);
        var grid=Id(main,"LibraryGrid");
        El Box(string title)=>Find(grid,El.NameProperty,"勾选 "+title)??throw new Exception("missing checkbox for "+title);
        string[] archive={"Amber Archive","Birch Archive","Cinder Archive","Dusk Archive","Elm Archive"};
        void Checked(params string[] names)
        {
            var expected=names.ToHashSet();
            Until(()=>archive.All(name=>On(Box(name))==expected.Contains(name)),"checked games: "+string.Join(", ",names));
        }
        void Menu(string command)
        {
            Until(()=>IsVisible(main,El.AutomationIdProperty,"BatchSelectButton") && Id(main,"BatchSelectButton").Current.IsEnabled,"bulk menu ready between commands");
            PointerClick(Id(main,"BatchSelectButton"));
            PointerClick(PopupItem(El.NameProperty,command));
        }
        void GridKeys(string keys)
        {
            grid.SetFocus();SetForegroundWindow((IntPtr)main.Current.NativeWindowHandle);
            System.Windows.Forms.SendKeys.SendWait(keys);
        }
        Assert(On(Find(grid,El.NameProperty,"勾选当前列表")!) && Id(main,"SelectionSummary").Current.Name.Contains("8 / 8"),"all eight games are initially checked");
        Toggle(Box("Slay the Spire"),false);
        Search("archive");Until(()=>Rows()==5,"filtered archive selection");
        ClickCell(grid,1);Checked("Birch Archive");
        ClickCell(grid,3,0x11);Checked("Birch Archive","Dusk Archive");
        ClickCell(grid,3,0x11);Checked("Birch Archive");
        ClickCell(grid,1);ClickCell(grid,4,0x10);Checked(archive[1..]);
        PointerClick(Box("Cinder Archive"));Checked("Birch Archive","Dusk Archive","Elm Archive");
        PointerClick(Id(grid,"RowEdit_9001103"));
        var editor=EditorFor("Cinder Archive");Click(Button(editor,"取消"));Until(Ready,"edit a title without changing the selection");
        Checked("Birch Archive","Dusk Archive","Elm Archive");
        Menu("取消勾选当前结果");Checked();
        Menu("只勾选可分类的游戏");Checked("Cinder Archive","Dusk Archive","Elm Archive");
        Menu("反选当前结果");Checked("Amber Archive","Birch Archive");
        Menu("只勾选可翻译的游戏");Checked(archive);
        GridKeys("^+a");Checked();
        PointerClick(Box("Amber Archive"));
        keybd_event(0x10,0,0,UIntPtr.Zero);
        try{PointerClick(Box("Dusk Archive"));}
        finally{keybd_event(0x10,0,2,UIntPtr.Zero);}
        Checked(archive[..4]);
        GridKeys("^a");Checked(archive);
        Menu("反选当前结果");Checked();
        Menu("勾选当前结果");Checked(archive);
        Search("");Until(()=>Rows()==8,"restore scope after checkbox commands");
        Assert(!On(Box("Slay the Spire")) && On(Box("Frost Notebook")) && On(Box("Flint Field Notes")),
            "ranged/menu/keyboard selection changed hidden rows");
        Menu("只勾选可翻译的游戏");
        Assert(On(Box("Amber Archive")) && On(Box("Birch Archive")) && !On(Box("Frost Notebook")) && !On(Box("Flint Field Notes")),
            "bulk translation selection included unconfirmed machine titles or excluded missing/official ones");
        Assert(before.SequenceEqual(FilesIn(root)) && BackupFolders(data).Count==0,"selection commands wrote Steam files");
        ClickCell(grid,2);ClickCell(grid,4,0x11);
        Until(()=>Id(main,"SelectionSummary").Current.Name.Contains("2 / 8") && On(Box("Cinder Archive")) && On(Box("Elm Archive")),
            "two clicked visible rows are the direct action scope");
        Screenshot(main,Path.Combine(baseDir,"selected-two.png"));
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var cache=Path.Combine(root,"appcache","appinfo.vdf");
        var originalCache=Sha(cache);
        PointerClick(Id(main,"ClassifyButton"));
        Until(()=>BackupFolders(data).Count==1 && Ready() && ManagedAssignments(ns).SequenceEqual(new[]{9001103,9001105}),"row selection exactly matches on-disk collection IDs");
        Assert(Sha(cache)==originalCache,"a classification changed a game name");
        Menu("勾选当前结果");Until(()=>Id(main,"SelectionSummary").Current.Name.Contains("8 / 8"),"all eight checked before pasted subset");
        Search("9001104, 646570");Until(()=>Rows()==2,"comma-delimited AppID input");
        Assert(TreeText(grid).Contains("Dusk Archive") && TreeText(grid).Contains("Slay the Spire"),"comma-separated exact AppIDs selected the wrong pair");
        Search("646570 9001104");Until(()=>Rows()==2 && ((ValuePattern)Id(main,"Search").GetCurrentPattern(ValuePattern.Pattern)).Current.Value.StartsWith("646570"),"normalized space-delimited AppID input");
        var multiset=TreeText(grid);
        Assert(multiset.Contains("Dusk Archive") && multiset.Contains("Slay the Spire") && Id(main,"SelectionSummary").Current.Name.Contains("2 / 2"),"normalized AppID input lost a selected ID");
        PointerClick(Id(main,"ClassifyButton"));
        Until(()=>BackupFolders(data).Count==2 && Ready() && ManagedAssignments(ns).SequenceEqual(new[]{646570,9001103,9001104,9001105}),"multiple AppIDs write only two games despite other games remaining checked");
        Assert(Sha(cache)==originalCache,"the two scoped group writes changed game names");
        Log("Real mouse, Ctrl, Shift and checkbox states agreed with bulk writes. Comma- and space-separated AppID input selected the exact same pair; still-checked hidden games were untouched.");
    }
    static void SmallFirstScreen(string root,string baseDir,string exe)
    {
        var grid=Id(main,"LibraryGrid");
        Assert(IsPage("游戏库"),"the active page is announced by its navigation item");
        foreach(var id in new[]{"PageTitle","InstalledFilter","GroupFilter","PreviewOptionsButton","ChangeNames","ChangeGroups",
            "SelectionDetail","ScanButton","ReviewLookupButton","EditButton","BulkEditButton","BackupsButton","SettingsButton"})
            Assert(!IsVisible(main,El.AutomationIdProperty,id),"secondary control is permanently visible on startup: "+id);
        foreach(var id in new[]{"FilterButton","MoreButton","Search","ClassifyButton","TranslateButton","BatchSelectButton","PreviewButton","CreateAllGamesButton"})
            Assert(IsVisible(main,El.AutomationIdProperty,id),"missing primary first-screen action "+id);
        var status=Find(main,El.AutomationIdProperty,"StatusText");
        Assert(status==null || status.Current.IsOffscreen || string.IsNullOrWhiteSpace(status.Current.Name),"scan success left a permanent status caption");
        var headers=grid.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.HeaderItem))
            .Cast<El>().Where(e=>!e.Current.IsOffscreen).Select(e=>e.Current.Name+" "+TreeText(e)).ToArray();
        Assert(headers.Length is 3 or 4 && !headers.Any(t=>t.Contains("状态") || t.Contains("操作")) &&
            headers.Any(t=>t.Contains("游戏")) && headers.Any(t=>t.Contains("中文名")) && headers.Any(t=>t.Contains("收藏集")),
            "expected game, Chinese name and collection column headers: "+string.Join(" | ",headers));
        Screenshot(main,Path.Combine(baseDir,"one-game-startup.png"));
        var baseline=FilesIn(root);
        var title=Id(grid,"RowEdit_646570");
        Assert(title.Current.Name=="编辑 Slay the Spire","game-title editor lacks a contextual accessible name");
        var count=pointerActivations;
        PointerClick(title);
        var editor=EditorFor("Slay the Spire");
        Assert(pointerActivations-count==1,"game-title edit required another activation");
        Click(Button(editor,"取消"));Until(Ready,"return from game-name editor");
        var gp=(GridPattern)grid.GetCurrentPattern(GridPattern.Pattern);
        var cell=gp.GetItem(0,gp.Current.ColumnCount-1);
        PointerClick(cell);
        cell.SetFocus();
        Until(()=>El.FocusedElement.Current.ProcessId==pid && On(Find(grid,El.NameProperty,"勾选 Slay the Spire")!),
            "selected non-button cell has keyboard focus");
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        editor=EditorFor("Slay the Spire");Click(Button(editor,"取消"));Until(Ready,"return from keyboard editor");

        Assert(baseline.SequenceEqual(FilesIn(root)) && On(Find(grid,El.NameProperty,"勾选 Slay the Spire")!),
            "visiting the game-name editor wrote Steam or changed its checkmark");
        PointerClick(Id(main,"MoreButton"));
        var settingsCommand=PopupItem(El.AutomationIdProperty,"SettingsButton");
        var commandList=TreeWalker.ControlViewWalker.GetParent(settingsCommand);
        Assert(Find(commandList,El.AutomationIdProperty,"MultiEditButton")==null && Find(commandList,El.AutomationIdProperty,"ReviewLookupButton")==null,
            "obsolete duplicate menu commands are still accessible");
        PointerClick(settingsCommand);
        var settings=Window("设置");
        var account=settings.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(El.ControlTypeProperty,ControlType.Edit),
            new PropertyCondition(El.NameProperty,"Steam 账号目录编号（留空自动识别）")));
        ((ValuePattern)account.GetCurrentPattern(ValuePattern.Pattern)).SetValue("invalid");
        Click(Button(settings,"保存并扫描"));
        Until(()=>Id(settings,"TaskError").Current.Name.Contains("只能包含数字"),"invalid account produces inline validation");
        Window("设置");
        Click(Button(settings,"取消"));Until(Ready,"cancel settings without writing");
        Assert(baseline.SequenceEqual(FilesIn(root)),"invalid settings changed Steam");
        var hwnd=(IntPtr)main.Current.NativeWindowHandle;
        ShowWindow(hwnd,6);Until(()=>IsIconic(hwnd),"minimize fixture before launching it a second time");
        using var duplicate=Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe)!,UseShellExecute=false})!;
        Assert(duplicate.WaitForExit(6000) && duplicate.ExitCode==0,"duplicate launch did not exit");
        Until(()=>!IsIconic(hwnd) && Ready() && Rows()==1,"second launch restores the original single window");
        Assert(baseline.SequenceEqual(FilesIn(root)),"duplicate launch changed Steam");
        Log("A one-game startup exposed three data columns and collapsed filters. A name click and Enter opened the same inline editor. Settings validation stayed in the same window. A second launch restored the minimized original and exited without writing.");
    }
    static void Receipt(string root,string data,string baseDir)
    {
        string paste=Path.Combine(data,"paste.result");
        Until(()=>File.Exists(paste) && File.ReadAllText(paste).Length>0,"in-memory WPF paste verification");
        Assert(File.ReadAllText(paste)=="passed" && Rows()==8 && ((ValuePattern)Id(main,"Search").GetCurrentPattern(ValuePattern.Pattern)).Current.Value=="",
            "real TextBox paste failed or left the query uncleared: "+File.ReadAllText(paste));
        Log("A WPF TextBox paste sourced from a memory DataObject preserved three exact IDs across CRLF/LF, filtered to those three, then cleared the query.");
        var cache=Path.Combine(root,"appcache","appinfo.vdf");
        var before=V29Records(cache);
        var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
        var priorCloud=Sha(ns);
        void Mixed(string query,string key,int trusted,int snapshots)
        {
            Search(query);Until(()=>Rows()==2 && Id(main,"TranslateButton").Current.IsEnabled,"mixed machine and trusted pair");
            PointerClick(Id(main,"TranslateButton"));
            Until(()=>File.Exists(Path.Combine(data,key+".started")) && BackupFolders(data).Count==snapshots && Ready() &&
                Id(main,"StatusText").Current.Name.Contains("改名 1 款") && !before[trusted].SequenceEqual(V29Records(cache)[trusted]),
                "trusted translation writes before independently gated machine reply");
        }
        void Release(string key)=>File.WriteAllText(Path.Combine(data,key+".release"),"");
        void Available()=>Until(()=>IsVisible(main,El.AutomationIdProperty,"CandidateReviewButton") &&
            Id(main,"CandidateReviewButton").Current.Name.Contains("1 个译名待核对"),"completed machine batch offers one candidate");
        Mixed("9001101,9001102","amber",9001102,1);
        Release("amber");Available();
        Assert(Id(main,"StatusText").Current.Name.Contains("改名 1 款") && V29Records(cache)[9001101].SequenceEqual(before[9001101]) && Sha(ns)==priorCloud,
            "machine completion overwrote the trusted result or silently wrote the machine title");
        Search("");Until(()=>Rows()==8,"full library with both outcomes");
        Screenshot(main,Path.Combine(baseDir,"machine-ready.png"));
        PointerClick(Id(main,"CandidateReviewButton"));
        var preview=Window("预览更改");
        Assert(Count(Find(preview,El.ControlTypeProperty,ControlType.DataGrid)!)==1 && !Id(preview,"PreviewApply").Current.IsEnabled &&
            !On(Id(preview,"PreviewName_9001101")) && !IsVisible(preview,El.AutomationIdProperty,"PreviewGroup_9001101") &&
            !IsVisible(preview,El.AutomationIdProperty,"PreviewGame_9001107"),"candidate review contains other games, groups or automatically selected names");
        Screenshot(preview,Path.Combine(baseDir,"machine-review.png"));
        Click(Button(preview,"返回"));Available();
        PointerClick(Id(main,"CandidateReviewButton"));preview=Window("预览更改");
        Toggle(Id(preview,"PreviewName_9001101"),true);
        Click(Id(preview,"PreviewApply"));
        Until(()=>BackupFolders(data).Count==2 && Ready() && !IsVisible(main,El.AutomationIdProperty,"CandidateReviewButton"),
            "adopting all current-batch candidates dismisses that receipt");
        Assert(Encoding.UTF8.GetString(V29Records(cache)[9001101]).Contains("琥珀档案") && Sha(ns)==priorCloud,"explicit confirmation missed its name or wrote a collection");
        Mixed("9001103,9001105","cinder",9001105,3);
        Search("646570");Until(()=>Rows()==1 && Id(main,"ClassifyButton").Current.IsEnabled,"newer task available during background candidate lookup");
        PointerClick(Id(main,"ClassifyButton"));
        Until(()=>BackupFolders(data).Count==4 && Ready() && Id(main,"StatusText").Current.Name.Contains("归类 1 款") &&
            ManagedAssignments(ns).SequenceEqual(new[]{646570}),"newer classification independently completed");
        string newerStatus=Id(main,"StatusText").Current.Name;
        Release("cinder");Available();
        Assert(Id(main,"StatusText").Current.Name==newerStatus && V29Records(cache)[9001103].SequenceEqual(before[9001103]) && BackupFolders(data).Count==4,
            "late candidate replaced the newer write result or modified Steam without confirmation");
        Log("Injected local JSON in the same WPF source: mixed translation wrote a trusted name, then showed one current-batch machine review beside the receipt. Review started unchecked, cancel retained it, explicit approval wrote only that name, and a later machine completion did not replace an intervening classification result.");
    }
    static void SourceAndPortable(string root,string data,string baseDir)
    {
        var initial=FilesIn(root);
        void OpenSelector(string title,string name)
        {
            El? dialog=null, field=null;
            Until(()=>{
                dialog=Windows().FirstOrDefault(el=>el.Current.Name==title);
                if(dialog==null)return false;
                field=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(El.ControlTypeProperty,ControlType.Edit))
                    .Cast<El>().FirstOrDefault(el=>!el.Current.IsOffscreen &&
                        (el.Current.AutomationId=="1001" || el.Current.Name.StartsWith("文件名")) &&
                        el.TryGetCurrentPattern(ValuePattern.Pattern,out _));
                return field!=null;
            },"native "+title);
            field!.SetFocus();
            var value=(ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern);
            value.SetValue(name);
            Until(()=>value.Current.Value==name,"file picker has the requested path");
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }
        var game=Id(Id(main,"LibraryGrid"),"RowEdit_646570");
        Click(game);
        var edit=EditorFor("Slay the Spire");
        var list=Id(edit,"NameCandidates");
        ((ExpandCollapsePattern)list.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Until(()=>IsVisible(edit,El.NameProperty,"杀戮尖塔 · 小黑盒 · 离线词典"),"HeyBox and dictionary share one title");
        Assert(Id(edit,"CopyAppId").Current.IsEnabled,"copy AppID is reachable in the editor");
        var candidate=Button(edit,"备选中文名 · Steam 内置");
        Click(candidate);
        Until(()=>((ValuePattern)Id(edit,"NameInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value=="备选中文名" &&
            On(Id(edit,"PinName")),"candidate selected for manual preference");
        Screenshot(edit,Path.Combine(baseDir,"name-choices.png"));
        Click(Id(edit,"EditSave"));
        Until(()=>Ready() && File.ReadAllText(Path.Combine(data,"suggestions.v2.json")).Contains("ManualName"),"candidate preference saved");

        var bundle=Path.Combine(baseDir,"portable-export.zip");
        MoreAction("BackupsButton");
        var backups=Window("备份与还原");
        Screenshot(backups,Path.Combine(baseDir,"portable-page.png"));
        Click(Id(backups,"ExportPortable"));
        OpenSelector("导出迁移包",bundle);
        Until(()=>File.Exists(bundle) && Ready(),"migration package exported");
        Click(Id(Id(main,"LibraryGrid"),"RowEdit_646570"));
        edit=EditorFor("Slay the Spire");
        ((ValuePattern)Id(edit,"NameInput").GetCurrentPattern(ValuePattern.Pattern)).SetValue("待覆盖的名称");
        Click(Id(edit,"EditSave"));Until(Ready,"modified the saved name");
        MoreAction("BackupsButton");backups=Window("备份与还原");
        Click(Id(backups,"ImportPortable"));
        OpenSelector("选择 Steam 统译迁移包",bundle);
        Click(Id(Window("导入迁移包"),"ConfirmPortableImport"));
        Until(()=>Ready() && Id(main,"StatusText").Current.Name.Contains("已导入") &&
            Directory.GetFiles(Path.Combine(data,"backups","portable"),"*.zip").Length==1,"migration package imported and before-copy saved");
        Assert(TreeText(Id(main,"LibraryGrid")).Contains("备选中文名") && initial.SequenceEqual(FilesIn(root)),
            "import restores the selected name while Steam bytes remain identical");
        Log("Candidate panel shows both conflicting sources inside the editor; portable ZIP round-trips saved preferences.");
    }

    static void Run(string publish,string baseDir,string variant)
    {
        var root=Path.GetFullPath(Path.Combine(baseDir,variant switch { "advanced"=>"v29", "undo"=>"v30", _=>variant }));
        Assert(File.Exists(Path.Combine(root,"steamapps","appmanifest_646570.acf")),"fixture missing");
        string app=Path.Combine(baseDir,"app-"+variant);
        CopyApp(variant=="receipt"?Path.Combine(baseDir,"replay-publish"):publish,app);
        var data=Path.Combine(app,"data");
        Directory.CreateDirectory(data);
        using var proxy=new TcpListener(IPAddress.Loopback,0);
        var proxyUrl="";
        if(variant is "advanced" or "workbench" or "direct")
        {
            proxy.Start();
            proxyUrl="http://127.0.0.1:"+((IPEndPoint)proxy.LocalEndpoint).Port;
        }
        var settings=variant is "direct" or "tiny"?JsonSerializer.Serialize(new {Root=root,Account="1",RestartSteam=false,Proxy=proxyUrl}):
            JsonSerializer.Serialize(new {Root=root,Account="1",AllCached=true,IncludeTools=false,RestartSteam=false,Proxy=proxyUrl,AutoLookup=false});
        File.WriteAllText(Path.Combine(data,"desktop.json"),settings);
        if(variant is "workbench" or "facets" or "workspace" or "direct" or "selection" or "receipt")
        {
            var saved=new Dictionary<int,object>{
                [9001106]=new { OnlineName="燧石手记", OnlineSource="机器翻译" }
            };
            if(variant is "facets" or "workspace" or "direct" or "selection" or "receipt")saved[9001107]=new {OnlineName="霜原手记",OnlineSource="机器翻译"};
            File.WriteAllText(Path.Combine(data,"suggestions.v2.json"),JsonSerializer.Serialize(saved));
        }
        var exe=Path.Combine(app,"SteamUnify.exe");
        var clock=Stopwatch.StartNew();
        using var proc=Process.Start(new ProcessStartInfo(exe){WorkingDirectory=app,UseShellExecute=false})!;
        pid=proc.Id;
        try
        {
            main=Window("Steam 统译");ActivateFixture();
            int expected=variant switch { "tiny" or "extras"=>1, "large"=>2500, "workbench"=>7, "facets" or "workspace" or "direct" or "selection" or "receipt"=>8, _=>12 };
            Until(()=>Rows()==expected && Id(main,"PreviewButton").Current.IsEnabled,"waiting for "+expected+" games",35);
            Log($"{variant}: launch-to-list {clock.ElapsedMilliseconds} ms, {Rows()} rows");
            if(variant=="tiny")
            {
                Assert(Id(main,"ClassifyButton").Current.IsEnabled && Id(main,"TranslateButton").Current.IsEnabled &&
                    TreeText(Id(main,"LibraryGrid")).Contains("Slay the Spire"),"single-record cold start did not expose both actions");
                SmallFirstScreen(root,baseDir,exe);
                return;
            }
            if(variant=="direct"){DirectActions(root,baseDir,data,proxy);return;}
            if(variant=="receipt"){Receipt(root,data,baseDir);return;}
            if(variant=="extras"){SourceAndPortable(root,data,baseDir);return;}
            if(variant=="selection"){Selection(root,data,baseDir);return;}
            if(variant=="advanced"){Advanced(root,data,proxy);return;}
            if(variant=="undo"){UndoAll(root);return;}
            if(variant=="workbench"){Workbench(root,baseDir,proxy);return;}
            if(variant=="facets"){Facets(root,baseDir,data);return;}
            if(variant=="dynamic"){DynamicCollection(root,baseDir,data,proc,exe);return;}
            if(variant=="workspace"){Workspace(root,baseDir,data);return;}
            if(variant=="v29")
            {
                Screenshot(main,Path.Combine(baseDir,"main.png"));
                var demoGrid=Id(main,"LibraryGrid");
                ClickCell(demoGrid,1);ClickCell(demoGrid,4,0x11);
                Until(()=>Id(main,"SelectionSummary").Current.Name.Contains("2 / 12"),"two visible ordinary game rows selected for review");
                Screenshot(main,Path.Combine(baseDir,"selected-two-library.png"));
                Toggle(Find(demoGrid,El.NameProperty,"勾选当前列表")!,true);
                NarrowScreenshot(baseDir);
                Log("Primary views: "+TreeText(Id(main,"Navigation")));
                Assert(Id(main,"Navigation").FindAll(TreeScope.Children,new PropertyCondition(El.ControlTypeProperty,ControlType.ListItem)).Count==3,"expected three workspaces");
                Click(Id(main,"PreviewButton"));
                var allPreview=Window("预览更改");
                Assert(VisibleMachineSources(allPreview)==0,"official fixture names were labeled machine-translated");
                Screenshot(allPreview,Path.Combine(baseDir,"preview.png"));
                Click(Button(allPreview,"返回"));
                Until(()=>Id(main,"PreviewButton").Current.IsEnabled,"dismiss full-library preview");
            }
            if(variant=="large")
            {
                var timer=Stopwatch.StartNew();Search("3902487");Until(()=>Rows()==1,"filter large library");
                Log("2500 → one AppID in "+timer.ElapsedMilliseconds+" ms (includes UIAutomation and debounce)");
                Search("");Until(()=>Rows()==2500,"clear filter");
                var grid=Id(main,"LibraryGrid");
                var scroll=(ScrollPattern)grid.GetCurrentPattern(ScrollPattern.Pattern);
                scroll.SetScrollPercent(ScrollPattern.NoScroll,100);
                Until(()=>scroll.Current.VerticalScrollPercent>95,"scroll to last page");
                Search("Library Game");Until(()=>Rows()==2488 && Id(main,"ClassifyButton").Current.IsEnabled && Id(main,"TranslateButton").Current.IsEnabled,
                    "known 2,488-game selection inside the 2,500-game fixture");
                var largeCache=Path.Combine(root,"appcache","appinfo.vdf");
                var original=V29Records(largeCache);
                var cloudFile=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
                var expectedIds=Enumerable.Range(3900000,2488).ToArray();
                var elapsed=Stopwatch.StartNew();
                PointerClick(Id(main,"ClassifyButton"));
                Until(()=>BackupFolders(data).Count==1 && Ready() && Id(main,"StatusText").Current.Name.Contains("已应用"),"batch classification finished and verified",90);
                var groupMs=elapsed.ElapsedMilliseconds;
                Assert(ManagedAssignments(cloudFile).SequenceEqual(expectedIds),"bulk classification touched a hidden game or missed a selected game");
                var cloudHash=Sha(cloudFile);
                elapsed.Restart();PointerClick(Id(main,"TranslateButton"));
                Until(()=>BackupFolders(data).Count==2 && Ready() && Id(main,"StatusText").Current.Name.Contains("已应用") &&
                    !Id(main,"TranslateButton").Current.IsEnabled,"batch translation finished and verified",90);
                var nameMs=elapsed.ElapsedMilliseconds;
                var rewritten=V29Records(largeCache);
                var changed=original.Where(kv=>!kv.Value.SequenceEqual(rewritten[kv.Key])).Select(kv=>kv.Key).Order().ToArray();
                Assert(changed.SequenceEqual(expectedIds) && Sha(cloudFile)==cloudHash &&
                    Encoding.UTF8.GetString(rewritten[3902487]).Contains("模拟游戏 2487"),"large batch failed field, inclusion or exact-name check");
                Log($"2,488 checked games: one-click classification {groupMs} ms; one-click local translation {nameMs} ms. Both measurements include backup, file writes, refresh, verification and UIAutomation observation. All 12 out-of-scope games remained unchanged.");
                return;
            }
            Search("no-match-1234");Until(()=>Rows()==0,"empty filter");
            Search("646570");Until(()=>Rows()==1,"single AppID filter");
            var acf=Path.Combine(root,"steamapps","appmanifest_646570.acf");
            var cache=variant=="v29"?Path.Combine(root,"appcache","appinfo.vdf"):Path.Combine(root,"appcache","appinfo","appinfo_01.vdf");
            var ns=Path.Combine(root,"userdata","1","config","cloudstorage","cloud-storage-namespace-1.json");
            string baselineCache=Sha(cache), baselineNs=Sha(ns), baselineAcf=Sha(acf);
            foreach(var groupsOnly in new[]{true,false})
            {
                Click(Id(main,"PreviewButton"));var preview=Window("预览更改");
                Toggle(Id(preview,groupsOnly?"PreviewAllNames":"PreviewAllGroups"),false);
                var screen=TreeText(preview);
                var previewGrid=Find(preview,El.ControlTypeProperty,ControlType.DataGrid)!;
                var gp=(GridPattern)previewGrid.GetCurrentPattern(GridPattern.Pattern);
                Assert(gp.Current.RowCount==1,"preview showed unselected games");
                Assert(gp.Current.ColumnCount==3,"both available fields remain visible after excluding a column");
                for(int col=0;col<gp.Current.ColumnCount;col++)
                    if(gp.GetItem(0,col).TryGetCurrentPattern(ValuePattern.Pattern,out var value))
                        screen+=" | "+((ValuePattern)value).Current.Value;
                Log("Preview: "+screen);
                Assert(screen.Contains(groupsOnly?"0 个名称":"1 个名称") && screen.Contains(groupsOnly?"1 项收藏集归属":"0 项收藏集归属"),"preview counts wrong");
                Assert(screen.Contains("Slay the Spire") && screen.Contains(groupsOnly?"策略与经营":"杀戮尖塔"),"preview item missing");
                if(variant=="v29" && groupsOnly)
                {
                    Screenshot(preview,Path.Combine(baseDir,"preview-single.png"));
                    Click(Button(preview,"返回"));Until(()=>Id(main,"PreviewButton").Current.IsEnabled,"cancel preview");
                    Assert(baselineCache==Sha(cache) && baselineNs==Sha(ns),"cancel changed Steam");
                    Click(Id(main,"PreviewButton"));preview=Window("预览更改");
                    Toggle(Id(preview,"PreviewAllNames"),false);
                }
                Click(Button(preview,"应用到 Steam"));
                Until(()=>groupsOnly?Sha(ns)!=baselineNs:Sha(cache)!=baselineCache,"apply fixture");
                Until(()=>Ready(),"backup control ready");
                if(groupsOnly)
                {
                    Assert(baselineCache==Sha(cache) && baselineAcf==Sha(acf),"group-only modified names");
                    using var records=JsonDocument.Parse(File.ReadAllBytes(ns));
                    var entries=records.RootElement.EnumerateArray().Select(pair=>pair[0].GetString()).ToList();
                    Assert(entries.Contains("user-collections.personal") && entries.Count==2,"private collection modified");
                }
                else
                {
                    Assert(Sha(ns)==baselineNs && Sha(acf)!=baselineAcf && File.ReadAllText(acf).Contains("杀戮尖塔"),"name-only changed collections or missed ACF");
                    Search("Slay the Spire");
                    Until(()=>Rows()==1 && TreeText(Id(main,"LibraryGrid")).Contains("杀戮尖塔"),"original name search finds the renamed game");
                }
                Log($"{variant}: {(groupsOnly?"group":"name")}-only apply and snapshot OK");
                Restore();
                Until(()=>Sha(cache)==baselineCache && Sha(ns)==baselineNs && Sha(acf)==baselineAcf,"restore originals");
                Until(()=>Ready() && Rows()==1,"reload after restore");
                Log($"{variant}: byte-exact restoration OK");
            }
            if(variant=="v29")
            {
                var original=File.ReadAllBytes(cache);
                File.WriteAllBytes(cache,new byte[16]);
                MoreAction("ScanButton");
                Until(()=>Rows()==0 && Ready() && Id(main,"EmptyTitle").Current.Name=="未能读取游戏库","scan format error");
                File.WriteAllBytes(cache,original);
                MoreAction("ScanButton");Until(()=>Rows()==1 && Ready(),"recovery after valid cache restored");
                Log("Unsupported cache showed a recoverable scan error; rescan recovered.");
            }
        }
        catch
        {
            foreach(var window in Windows())
            {
                try{Log("Window: "+window.Current.Name+" "+TreeText(window)[..Math.Min(3300,TreeText(window).Length)]);}catch{}
            }
            throw;
        }
        finally
        {
            if(!proc.HasExited&&!proc.CloseMainWindow())proc.Kill();
            if(!proc.WaitForExit(2000))proc.Kill();
        }
    }
    [MTAThread]
    static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4));
        var dir=Path.GetFullPath(args[1]); logPath=Path.Combine(dir,"ui-results.txt");
        try
        {
            var publish=Path.GetFullPath(args[0]);
            foreach(var variant in args.Skip(2))
            {
                Run(publish,dir,variant);
                if(variant=="direct")Run(publish,dir,"tiny");
            }
            Log("All selected desktop acceptance cases passed."); return 0;
        }
        catch(Exception e){Log(e.ToString());return 1;}
    }
}
