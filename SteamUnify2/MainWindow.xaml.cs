using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SteamUnify;

public partial class MainWindow : Window
{
    private readonly string dataDirectory;
    private DesktopOptions options;
    private readonly Suggestions suggestions;
    private SteamLocation? location;
    private string? allGamesCollectionName;
    private LibraryInfo? library;
    private (SteamLocation Account,string Path)? lastWriteBackup;
    private enum ReviewReason { All, Name, Group, Machine }
    private ReviewReason reviewReason;
    private List<GameRow> rows = [];
    private ICollectionView? view;
    private string section = "all";
    private bool onlyInstalled, updatingFacets;
    private string? chosenGroup;
    private LibrarySearch search = new("");
    private string? scanError;
    private bool ready, busy, navigating, closingWhenDone;
    private CancellationTokenSource? cancellation;
    private CheckBox? selectAllBox;
    private bool syncingSelectAll;
    private readonly DispatcherTimer searchTimer = new() { Interval=TimeSpan.FromMilliseconds(100) };

    public MainWindow(string dataDirectory)
    {
        this.dataDirectory=dataDirectory;
        options=DesktopOptions.Load(dataDirectory);
        suggestions=new Suggestions(dataDirectory);
        InitializeComponent();
        WindowAppearance.MatchCaption(this);
        var desktop=SystemParameters.WorkArea;
        MinWidth=Math.Min(MinWidth,desktop.Width);
        MinHeight=Math.Min(MinHeight,desktop.Height);
        Width=Math.Min(Width,desktop.Width);
        Height=Math.Min(Height,desktop.Height);
        searchTimer.Tick+=(_,_)=>{searchTimer.Stop(); Filter();};
        progressTimer.Tick+=(_,_)=>{RefreshCandidateNames();UpdateCandidateControls();DrawProgress();};
        KeyDown+=(_,e)=>{if(taskPage!=null && e.Key==Key.Escape){e.Handled=true;CloseTaskPage(false);}};
        ready=true;
        Loaded+=async (_,_)=>await ScanAsync();
        Closing+=OnClosing;
    }

    private async Task WorkAsync(string description, Func<Task> action, bool allowCancel=false)
    {
        if(busy) return;
        busy=true;
        lock(progressLock)latestProgress=default;
        cancellation=allowCancel?new CancellationTokenSource():null;
        StatusText.Text=description;
        UpdateControls();
        try { await action(); }
        catch(OperationCanceledException) when(cancellation?.IsCancellationRequested==true) { StatusText.Text="任务已停止，已完成的结果已保存。"; }
        catch(Exception ex)
        {
            StatusText.Text=ex.Message;
            if(location==null) {scanError=ex.Message; UpdateEmpty();}

        }
        finally
        {
            cancellation?.Dispose();cancellation=null;busy=false;RefreshCandidateNames();UpdateControls();
            if(closingWhenDone && candidateCancellation==null)Close();
        }
    }

    private void OnClosing(object? sender,CancelEventArgs e)
    {
        closingWhenDone=true;
        CloseTaskPage(false);
        candidateCancellation?.Cancel();
        if(!busy && candidateCancellation==null)return;
        e.Cancel=true;
        cancellation?.Cancel();
        StatusText.Text="正在保存已完成的结果…";
    }
    private void UpdateControls()
    {
        ScanButton.IsEnabled=SettingsButton.IsEnabled=!busy;
        BackupsButton.IsEnabled=!busy && location!=null;
        LastBackupButton.Visibility=lastWriteBackup is { } backup && backup.Account==location ? Visibility.Visible : Visibility.Collapsed;
        LastBackupButton.IsEnabled=!busy;
        LibraryGrid.Resources["RowEditingAllowed"]=!busy;
        Navigation.IsEnabled=LibraryGrid.IsEnabled=InstalledFilter.IsEnabled=GroupFilter.IsEnabled=Search.IsEnabled=!busy;
        ReviewReasonBar.IsEnabled=ClearFiltersButton.IsEnabled=ClearSearchButton.IsEnabled=CacheScopeButton.IsEnabled=!busy;
        UpdateAllGamesControl();
        MoreButton.IsEnabled=!busy;
        UpdateCandidateControls();
        FilterButton.IsEnabled=!busy;
        DrawProgress();
        UpdateSelection();
    }

    private void UpdateAllGamesControl()
    {
        bool exists=allGamesCollectionName!=null;
        AllGamesGlyph.Text=exists?"\uE73E":"\uE710";
        AllGamesActionLabel.Text=allGamesCollectionName??"创建「全部游戏」";
        CreateAllGamesButton.IsEnabled=!busy && location!=null && !exists;
        CreateAllGamesButton.ToolTip=exists?$"「{allGamesCollectionName}」由 Steam 自动更新。":
            "为当前 Steam 账号创建动态收藏，包含库中已玩和未玩的游戏，与当前筛选和勾选无关。";
        AutomationProperties.SetName(CreateAllGamesButton,exists?$"Steam 动态收藏「{allGamesCollectionName}」已就绪":"创建「全部游戏」动态收藏");
    }
    private sealed record ScanResult(SteamLocation Location,LibraryInfo Info,List<GameRow> Rows,List<string> Warnings,string? AllGamesCollectionName);
    private ScanResult ReadLibrary(IReadOnlySet<int>? selectedIds=null)
    {
        var steam=SteamLibrary.Detect(options.Root,options.Account);
        var info=SteamLibrary.Inspect(steam);
        var warnings=new List<string>();
        IReadOnlySet<int>? appIds=selectedIds;
        if(!options.AllCached)
        {
            var libraryIds=new HashSet<int>(info.Owned);
            libraryIds.UnionWith(info.Installed.Keys);
            if(selectedIds!=null)libraryIds.IntersectWith(selectedIds);
            appIds=libraryIds;
        }
        var entries=SteamCache.Read(steam.Root,warnings,appIds);
        var originals=SteamLibrary.OriginalNames(steam,dataDirectory,entries.ToDictionary(game=>game.AppId));
        var collections=new SteamCollections(steam,dataDirectory);
        var memberships=collections.Memberships();
        var games=entries.Where(game=>
        {
            return game.Type.ToLowerInvariant() switch { "game"=>true, "tool" or "application"=>options.IncludeTools, _=>false };
        }).ToArray();
        var proposed=suggestions.RecommendBatch(games,info.TagNames,originals);
        var result=new List<GameRow>(games.Length);
        for(int i=0;i<games.Length;i++)
        {
            var game=games[i];
            string original=originals.GetValueOrDefault(game.AppId,game.Name);
            result.Add(new() {Game=game,OriginalName=original,Installed=info.Installed.ContainsKey(game.AppId),
                CurrentGroups=memberships.GetValueOrDefault(game.AppId)??[],Suggested=proposed[i]});
        }
        if(selectedIds==null)result.Sort((a,b)=>StringComparer.CurrentCultureIgnoreCase.Compare(a.Game.Name,b.Game.Name));
        return new(steam,info,result,warnings,collections.AllGamesCollectionName);
    }

    private async Task<List<GameRow>> LoadAsync()
    {
        var previous=location;
        var uncheckedIds=rows.Where(r=>!r.Picked).Select(r=>r.Id).ToHashSet();
        ScanResult scan;
        try { scan=await Task.Run(()=>ReadLibrary()); }
        catch(Exception ex)
        {
            location=null;library=null;rows=[];allGamesCollectionName=null;scanError=ex.Message;
            Rebind();throw;
        }
        location=scan.Location;library=scan.Info;scanError=null;rows=scan.Rows;
        allGamesCollectionName=scan.AllGamesCollectionName;
        if(previous==location)
            foreach(var row in rows) row.Picked=!uncheckedIds.Contains(row.Id);
        else
        {
            section="all";
            ResetReviewReason();
            updatingFacets=true;
            InstalledFilter.IsChecked=false;
            onlyInstalled=false;
            chosenGroup=null;
            updatingFacets=false;
            if(previous!=null)Search.Clear();
        }
        Rebind();
        scanWarnings=scan.Warnings;
        StatusText.Text=scan.Warnings.Count>0?$"扫描完成，{scan.Warnings.Count} 条缓存提示。可在「更多」中查看。":"";
        return rows;
    }
    private List<string> scanWarnings=[];
    private async Task ScanAsync()
    {
        List<GameRow> fresh=[];
        await WorkAsync("正在扫描 Steam 游戏库…",async ()=>{
            await LoadAsync();
            if(!options.AutoLookup || location==null) return;
            string path=Path.Combine(dataDirectory,$"seen-{Path.GetFileNameWithoutExtension(SteamLibrary.OwnershipPath(location,dataDirectory))}.json");
            var seen=File.Exists(path)?JsonSerializer.Deserialize<HashSet<int>>(await File.ReadAllBytesAsync(path))??[]:[];
            fresh=rows.Where(r=>!seen.Contains(r.Id) && r.Name.Length==0 && !r.Suggested.NameLocked).ToList();
            seen.UnionWith(rows.Select(r=>r.Id));
            await File.WriteAllBytesAsync(path+".tmp",JsonSerializer.SerializeToUtf8Bytes(seen));
            File.Move(path+".tmp",path,true);
        });
        if(!closingWhenDone && fresh.Count>0) await OnlineAsync(fresh,false);
    }
    private void Rebind()
    {
        checkAnchor=null;
        RefreshGroupChoices();
        view=CollectionViewSource.GetDefaultView(rows);
        view.Filter=Matches;
        LibraryGrid.ItemsSource=view;
        Filter();
    }
    private void RefreshGroupChoices()
    {
        var used = rows.Select(row=>row.Group).Where(group=>group.Length>0).ToHashSet(StringComparer.Ordinal);
        if(chosenGroup!=null)used.Add(chosenGroup);
        var groups = Suggestions.Groups.Where(used.Contains)
            .Concat(used.Except(Suggestions.Groups).Order(StringComparer.CurrentCultureIgnoreCase)).ToList();
        int previous = chosenGroup==null ? -1 : groups.IndexOf(chosenGroup);
        updatingFacets=true;
        try
        {
            GroupFilter.ItemsSource=new[]{"全部建议收藏集"}.Concat(groups).ToArray();
            GroupFilter.SelectedIndex=previous+1;
            if(previous<0)chosenGroup=null;
        }
        finally { updatingFacets=false; }
    }
    private void BuildNavigation(int all,int review,int changes)
    {
        navigating=true;
        try
        {
            var primary=new[]{new LibraryLink("all","游戏库","\uE8F1",all),
                new LibraryLink("review","待确认","\uE7BA",review),
                new LibraryLink("changes","待应用","\uE73A",changes)};
            if(Navigation.ItemsSource is not LibraryLink[] previous || !previous.SequenceEqual(primary))
                Navigation.ItemsSource=primary;
            Navigation.SelectedItem=Navigation.Items.Cast<LibraryLink>().First(link=>link.Key==section);
        }
        finally{navigating=false;}
    }
    private void Navigate(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || navigating || ((ListBox)sender).SelectedItem is not LibraryLink link) return;
        if(section!=link.Key)ResetReviewReason();
        section=link.Key;
        Filter();
    }
    private void ResetReviewReason()
    {
        reviewReason=ReviewReason.All;
        ReviewAll.IsChecked=true;
    }
    private void ReasonChanged(object sender,RoutedEventArgs e)
    {
        if(!ready || updatingFacets)return;
        var reason=sender==ReviewMissingName ? ReviewReason.Name : sender==ReviewMissingGroup ? ReviewReason.Group :
            sender==ReviewMachine ? ReviewReason.Machine : ReviewReason.All;
        if(reason==reviewReason)return;
        reviewReason=reason;
        Filter();
    }
    private void FacetChanged(object sender,RoutedEventArgs e)
    {
        if(!ready || updatingFacets)return;
        onlyInstalled=InstalledFilter.IsChecked==true;
        chosenGroup=GroupFilter.SelectedIndex>0 ? GroupFilter.SelectedItem as string : null;
        Filter();
    }
    private void ClearFacets(object sender,RoutedEventArgs e)
    {
        updatingFacets=true;
        try
        {
            InstalledFilter.IsChecked=false;
            GroupFilter.SelectedIndex=0;
            ResetReviewReason();
        }
        finally { updatingFacets=false; }
        FacetChanged(sender,e);
    }
    private bool HasFacets => onlyInstalled || chosenGroup!=null || (section=="review" && reviewReason!=ReviewReason.All);
    private static bool HasPendingChange(GameRow row)=>row.HasDefaultChanges(true,true);
    private bool InScope(GameRow row)
    {
        if(onlyInstalled && !row.Installed || chosenGroup!=null && row.Group!=chosenGroup)return false;
        return search.Matches(row);
    }
    private bool Matches(object value)
    {
        var row=(GameRow)value;
        return InScope(row) && (section switch
        {
            "changes"=>HasPendingChange(row),
            "review"=>reviewReason switch
            {
                ReviewReason.Name=>row.NeedsName, ReviewReason.Group=>row.NeedsGroup,
                ReviewReason.Machine=>row.MachineReview, _=>row.Suggested.NeedsReview
            },
            _=>true
        });
    }
    private void Filter()
    {
        if(!ready)return;
        if(search.Text!=Search.Text)search=new(Search.Text);
        view?.Refresh();
        int all=0, review=0, changes=0, missingName=0, missingGroup=0, machine=0;
        foreach(var row in rows)
        {
            if(!InScope(row))continue;
            all++;
            if(row.Suggested.NeedsReview)review++;
            if(HasPendingChange(row))changes++;
            if(row.NeedsName)missingName++;
            if(row.NeedsGroup)missingGroup++;
            if(row.MachineReview)machine++;
        }
        BuildNavigation(all,review,changes);
        ReviewReasonBar.Visibility=section=="review"?Visibility.Visible:Visibility.Collapsed;
        if(section=="review")
        {
            ReviewAll.Content=$"全部 {review:N0}";
            ReviewMissingName.Content=$"缺中文名 {missingName:N0}";
            ReviewMissingGroup.Content=$"待选收藏集 {missingGroup:N0}";
            ReviewMachine.Content=$"核对机翻 {machine:N0}";
        }
        ClearFiltersButton.Visibility=HasFacets?Visibility.Visible:Visibility.Collapsed;
        FilterLabel.Text=onlyInstalled && chosenGroup!=null?$"仅已安装 · {chosenGroup}":chosenGroup??(onlyInstalled?"仅已安装":"筛选");
        AutomationProperties.SetName(FilterButton,FilterLabel.Text);
        FilterButton.ToolTip=onlyInstalled || chosenGroup!=null?FilterLabel.Text:"按安装状态或收藏集筛选";
        CacheScopeButton.Visibility=options.AllCached && location!=null?Visibility.Visible:Visibility.Collapsed;
        Navigation.ToolTip=location==null?null:$"Steam 账号 {location.Account} · {location.Root}";
        UpdateEmpty();UpdateSelection();
    }
    private void UpdateEmpty()
    {
        if(!ready)return;
        bool empty=view?.IsEmpty!=false;
        EmptyPanel.Visibility=empty?Visibility.Visible:Visibility.Collapsed;
        if(!empty)return;
        if(scanError!=null){EmptyTitle.Text="未能读取游戏库";EmptyDetail.Text=scanError;EmptyButton.Content="打开设置";}
        else if(rows.Count==0){EmptyTitle.Text=busy && location==null?"正在读取游戏库":"尚未找到游戏";EmptyDetail.Text=location==null?"":"可在设置中选择 Steam 目录、切换账号或包含共享游戏。";EmptyButton.Content="打开设置";}
        else if(search.IsActive)
        {EmptyTitle.Text="没有找到匹配的游戏";EmptyDetail.Text="";EmptyButton.Content="清除搜索";}
        else if(HasFacets)
        {EmptyTitle.Text="当前筛选下没有游戏";EmptyDetail.Text="";EmptyButton.Content="重置筛选";}
        else if(section=="review")
        {EmptyTitle.Text="没有待确认的游戏";EmptyDetail.Text="";EmptyButton.Content=rows.Any(HasPendingChange)?"查看待应用":"返回游戏库";}
        else if(section=="changes")
        {
            EmptyTitle.Text="当前没有待应用的更改";
            EmptyDetail.Text="";
            EmptyButton.Content=rows.Any(r=>r.Suggested.NeedsReview)?"查看待确认":"返回游戏库";
        }
        else{EmptyTitle.Text="此处没有游戏";EmptyDetail.Text="";EmptyButton.Content="返回游戏库";}
        EmptyDetail.Visibility=EmptyDetail.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        EmptyButton.IsEnabled=!busy;
    }
    private IEnumerable<GameRow> Visible() => view?.Cast<GameRow>()??Enumerable.Empty<GameRow>();
    private void UpdateSelection()
    {
        if(!ready)return;
        int count=0,picked=0,changed=0,pendingNames=0,missingNames=0;
        int directNames=0,directGroups=0;
        foreach(var row in Visible()){
            count++;
            if(!row.Picked)continue;
            picked++;
            if(CanTranslate(row))directNames++;
            if(row.HasGroupChange)directGroups++;
            if(HasPendingChange(row))changed++;
            if(row.MachineReview && row.HasNameChange)pendingNames++;
            if(row.NeedsName)missingNames++;
        }
        SelectionSummary.Text=$"已勾选 {picked:N0} / {count:N0} 款";
        PreviewButton.Content=changed>0?$"预览 {changed:N0} 款更改":pendingNames>0?$"查看 {pendingNames:N0} 个待核译名":"预览更改";
        PreviewButton.IsEnabled=!busy && (changed>0 || pendingNames>0);
        PreviewButton.ToolTip=pendingNames>0?$"默认已选 {changed:N0} 款更改，另有 {pendingNames:N0} 个机翻译名可逐项核对。":
            changed>0?$"检查 {changed:N0} 款游戏的具体更改（Ctrl+Enter）":
            picked==0?"先勾选游戏。":"勾选的游戏没有更改。";
        ClassifyButton.IsEnabled=!busy && directGroups>0;
        TranslateButton.IsEnabled=!busy && directNames>0;
        ClassifyButton.ToolTip=$"将当前勾选的 {directGroups:N0} 款游戏归入建议收藏集。写入前自动备份。";
        TranslateButton.ToolTip=$"处理当前勾选的 {directNames:N0} 款游戏，其中 {missingNames:N0} 款需联网查名。只写入已确认的译名，写入前自动备份。游戏名由本机 Steam 账号共享。";
        TranslateButton.Content=missingNames>0?"一键翻译（含联网）":"一键翻译";
        BatchSelectButton.IsEnabled=!busy && count>0;
        BulkEditButton.IsEnabled=!busy && picked>0;
        EnrichButton.IsEnabled=!busy && picked>0;
        ExportButton.IsEnabled=!busy && count>0;
        if(selectAllBox!=null)
        {
            syncingSelectAll=true;
            try { selectAllBox.IsChecked=picked==0?false:picked==count?true:null; }
            finally { syncingSelectAll=false; }
        }
        EmptyButton.IsEnabled=!busy;
    }
    private void HeaderLoaded(object sender,RoutedEventArgs e){selectAllBox=(CheckBox)sender;UpdateSelection();}
    private void ToggleVisible(object sender,RoutedEventArgs e)
    {
        if(!ready || syncingSelectAll || sender!=selectAllBox)return;
        FlushSearch();
        bool value=Visible().Any(row=>!row.Picked);
        SetVisiblePicks(_=>value);
    }
    private void RowChecked(object sender,RoutedEventArgs e)=>UpdateSelection();
    private void SearchChanged(object sender,TextChangedEventArgs e)
    {
        if(!ready)return;SearchHint.Visibility=Search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        ClearSearchButton.Visibility=Search.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        searchTimer.Stop();searchTimer.Start();
    }
    private void PasteSearch(object sender,DataObjectPastingEventArgs e)
    {
        if(e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted)return;
        string? line=LibrarySearch.NormalizePaste(Search.Text,Search.SelectionStart,Search.SelectionLength,pasted);
        if(line==null)return;
        e.DataObject=new DataObject(DataFormats.UnicodeText,line);
        e.FormatToApply=DataFormats.UnicodeText;
    }
    private void FlushSearch()
    {
        if(!searchTimer.IsEnabled)return;
        searchTimer.Stop();
        Filter();
    }
    private void EmptyAction(object sender,RoutedEventArgs e)
    {
        if(rows.Count==0){ShowSettings(sender,e);return;}
        if(search.IsActive){ClearSearch(sender,e);return;}
        if(HasFacets){ClearFacets(sender,e);return;}
        ResetReviewReason();
        section=section switch {
            "review" when rows.Any(HasPendingChange) => "changes",
            "changes" when rows.Any(r=>r.Suggested.NeedsReview) => "review",
            _ => "all"
        };
        Filter();
    }
    private void ClearSearch(object sender,RoutedEventArgs e)
    {
        Search.Clear();
        FlushSearch();
        Search.Focus();
    }
    private void WindowKeyDown(object sender,KeyEventArgs e)
    {
        if(taskPage!=null)return;
        if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.Control)
        {
            FlushSearch();
            if(PreviewButton.IsEnabled)ShowPreview(sender,e);
            e.Handled=true;
        }
        else if(e.Key==Key.F && Keyboard.Modifiers==ModifierKeys.Control)
        {
            if(busy)return;
            Search.Focus();Search.SelectAll();e.Handled=true;
        }
        else if(e.Key==Key.Escape && Search.IsKeyboardFocusWithin && Search.Text.Length>0)
        {
            ClearSearch(sender,e);e.Handled=true;
        }
    }
    private async void Rescan(object sender,RoutedEventArgs e)=>await ScanAsync();
    private void Stop(object sender,RoutedEventArgs e)
    {
        if(cancellation!=null)cancellation.Cancel();
        else candidateCancellation?.Cancel();
        UpdateCandidateControls();
    }
    private async Task RecalculateAsync(IReadOnlySet<int> updated)
    {
        if(library==null)return;
        var affected=rows.Where(row=>updated.Contains(row.Id)).ToArray();
        var tags=library.TagNames;
        var proposed=await Task.Run(()=>suggestions.RecommendBatch(affected.Select(row=>row.Game).ToArray(),tags,
            affected.ToDictionary(row=>row.Id,row=>row.OriginalName)));
        for(int i=0;i<affected.Length;i++)affected[i].Refresh(proposed[i]);
        RefreshGroupChoices();
        Filter();
    }
    private async void EditRow(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(busy || sender is not Button { DataContext:GameRow row })return;
        FlushSearch();
        if(view?.Contains(row)==true)await EditRowsAsync([row]);
    }
    private async void BulkEdit(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        FlushSearch();
        await EditRowsAsync(Visible().Where(row=>row.Picked).ToList(),allowReviewQueue:false);
    }
    private async Task EditRowsAsync(List<GameRow> selection,bool allowReviewQueue=true)
    {
        if(busy || selection.Count==0)return;
        var remaining=new Queue<GameRow>();
        if(allowReviewQueue && section=="review" && selection.Count==1)
        {
            var review=Visible().ToList();
            int start=review.IndexOf(selection[0]);
            if(start>=0)
                remaining=new Queue<GameRow>(review.Skip(start+1).Concat(review.Take(start)));
        }
        while(true)
        {
            var edit=await TaskDialog.Edit(this,selection,remaining.Count>0);
            if(edit==null)return;
            bool saved=false;
            await WorkAsync("正在保存手动设置…",async ()=>{
                await Task.Run(()=>{
                    foreach(var row in selection)
                    {
                        if(edit.Clear || selection.Count==1)suggestions.ClearManual(row.Id);
                        if(!edit.Clear)suggestions.SetManual(row.Id,edit.Name,edit.Group);
                    }
                    suggestions.Save();
                });
                await RecalculateAsync(selection.Select(r=>r.Id).ToHashSet());
                StatusText.Text=selection.Count==1?$"已保存「{selection[0].Game.Name}」的建议。":$"已更新 {selection.Count:N0} 款游戏的建议。";
                saved=true;
            });
            if(!saved || !edit.Continue || closingWhenDone || !remaining.TryDequeue(out var next))return;
            LibraryGrid.ScrollIntoView(next);
            selection=[next];
        }
    }
    private async void ShowPreview(object sender,RoutedEventArgs e)
    {
        if(busy || location is not { } steam)return;
        FlushSearch();
        var edits=Visible().Where(r=>r.Picked).Select(r=>new ChangeLine(r,new(r.Id,
            r.HasNameChange?r.Name:null,r.HasGroupChange?r.Group:null)))
            .Where(x=>x.Edit.Name!=null||x.Edit.Collection!=null).ToList();
        await PreviewAndApplyAsync(steam,edits);
    }

    private async Task PreviewAndApplyAsync(SteamLocation steam,List<ChangeLine> edits)
    {
        if(edits.Count==0 || steam!=location)return;
        var approved=await TaskDialog.Preview(this,edits,options.RestartSteam);
        if(approved==null || approved.Count==0 || steam!=location)return;
        await WriteApprovedAsync(steam,approved,()=>SteamLibrary.Apply(steam,approved.Select(x=>x.Edit).ToList(),dataDirectory,options.RestartSteam,ReportProgress));
    }
    private async Task<bool> WriteApprovedAsync(SteamLocation steam,List<ChangeLine> approved,Func<string> write,string remainder="")
    {
        bool verified=false;
        await WorkAsync("正在备份并写入 Steam…",async ()=>{
            string backup;
            try{backup=await Task.Run(write);}
            catch(StaleRecommendationException)
            {
                await LoadAsync();
                StatusText.Text="推荐已更新，请重新确认。此次没有写入。";
                return;
            }
            catch(OperationCanceledException){StatusText.Text="已取消整理。";return;}
            lastWriteBackup=backup.Length>0?(steam,backup):null;
            ReportProgress("复查写入结果",0,0);
            int mismatch=await RefreshWrittenFieldsAsync(steam,approved);
            verified=mismatch==0;
            var parts=new List<string>();
            int names=approved.Count(x=>x.Edit.Name!=null),groups=approved.Count(x=>x.Edit.Collection!=null);
            if(names>0)parts.Add($"改名 {names:N0} 款");
            if(groups>0)parts.Add($"归类 {groups:N0} 款");
            StatusText.Text=!verified?$"已写入，{mismatch:N0} 款游戏的复查结果有变化。":backup.Length==0?"Steam 已是目标状态。":
                "已应用并复查 · "+string.Join("  ·  ",parts)+remainder;
        });
        return verified;
    }

    private async Task<int> RefreshWrittenFieldsAsync(SteamLocation steam,List<ChangeLine> approved)
    {
        var renamed=approved.Where(line=>line.Edit.Name!=null).ToArray();
        bool classified=approved.Any(line=>line.Edit.Collection!=null);
        var account=await Task.Run(()=>SteamLibrary.Detect(options.Root,options.Account));
        if(account!=steam)
        {
            await LoadAsync();
            return approved.Count;
        }
        var namesTask=renamed.Length==0?Task.FromResult(new Dictionary<int,CacheGame>()):
            Task.Run(()=>SteamCache.Read(steam.Root,new(),renamed.Select(line=>line.Row.Id).ToHashSet()).ToDictionary(game=>game.AppId));
        var groupsTask=classified?Task.Run(()=>SteamLibrary.Memberships(steam,dataDirectory)):
            Task.FromResult(new Dictionary<int,List<string>>());
        await Task.WhenAll(namesTask,groupsTask);
        var games=await namesTask;
        var groups=await groupsTask;
        int mismatch=approved.Count(line=>
            line.Edit.Name!=null && (!games.TryGetValue(line.Row.Id,out var game) || game.Name!=line.Edit.Name) ||
            line.Edit.Collection!=null && (!groups.TryGetValue(line.Row.Id,out var current) || current.Count!=1 || current[0]!=line.Edit.Collection));

        var accepted=renamed.Where(line=>line.Row.MachineReview && games.TryGetValue(line.Row.Id,out var game) && game.Name==line.Edit.Name).ToArray();
        if(accepted.Length>0)await Task.Run(()=>{
            foreach(var line in accepted)suggestions.SetManual(line.Row.Id,line.Edit.Name,null);
            suggestions.Save();
        });
        if(mismatch>0){await LoadAsync();return mismatch;}

        var tags=library!.TagNames;
        var refreshed=renamed.Length==0?[]:await Task.Run(()=>suggestions.RecommendBatch(
            renamed.Select(line=>games[line.Row.Id]).ToArray(),tags,renamed.ToDictionary(line=>line.Row.Id,line=>line.Row.OriginalName)));
        for(int i=0;i<renamed.Length;i++)
        {
            var row=renamed[i].Row;
            row.Game=games[row.Id];
            row.Suggested=refreshed[i];
        }
        foreach(var line in approved)
        {
            if(line.Edit.Collection!=null)line.Row.CurrentGroups=groups[line.Row.Id];
            line.Row.Refresh(line.Row.Suggested);
        }
        if(renamed.Length>0)
        {
            rows.Sort((a,b)=>StringComparer.CurrentCultureIgnoreCase.Compare(a.Game.Name,b.Game.Name));
            RefreshGroupChoices();
        }
        Filter();
        return 0;
    }
    private async void ShowSettings(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var result=await TaskDialog.Settings(this,options);
        if(result==null)return;
        try
        {
            result.Save(dataDirectory);
            if(result.Root!=options.Root || result.Account!=options.Account || result.Proxy!=options.Proxy)candidateCancellation?.Cancel();
            options=result;
        }
        catch(Exception ex){StatusText.Text="设置保存失败。"+ex.Message;return;}
        await ScanAsync();
    }
    private async void CreateAllGames(object sender,RoutedEventArgs e)
    {
        if(busy || location is not { } steam || allGamesCollectionName!=null)return;
        await WorkAsync("正在准备动态收藏…",async ()=>{
            void ConfirmShutdown()
            {
                if(!Dispatcher.Invoke(()=>TaskDialog.ConfirmAllGames(this,options.RestartSteam)).GetAwaiter().GetResult())
                    throw new OperationCanceledException();
            }
            try
            {
                var result=await Task.Run(()=>SteamLibrary.EnsureAllGamesCollection(steam,dataDirectory,options.RestartSteam,ConfirmShutdown));
                allGamesCollectionName=result.Name;
                if(result.Created)lastWriteBackup=(steam,result.BackupPath);
                StatusText.Text=result.Created
                    ? $"已创建「{result.Name}」。可在 Steam 游戏库的收藏集中查看。"
                    : $"Steam 库中已有动态收藏「{result.Name}」。";
            }
            catch(OperationCanceledException) { StatusText.Text="已取消创建。"; }
        });
    }
    private async void ShowBackups(object sender,RoutedEventArgs e)=>await OpenBackupsAsync(null);
    private async void ShowLastBackup(object sender,RoutedEventArgs e)
    {
        if(lastWriteBackup is { } backup && backup.Account==location)
            await OpenBackupsAsync(backup.Path);
    }
    private async Task OpenBackupsAsync(string? preferredPath)
    {
        if(busy||location==null)return;
        var steam=location;
        string previousStatus=StatusText.Text;
        await WorkAsync("正在读取备份…",async ()=>{
            var backups=await Task.Run(()=>SteamLibrary.Backups(steam,dataDirectory));
            if(preferredPath!=null && !backups.Contains(preferredPath,StringComparer.OrdinalIgnoreCase))
            {
                lastWriteBackup=null;
                StatusText.Text="找不到这次写入的快照。可在「备份与还原」中查看其他备份。";
                return;
            }
            var choice=await TaskDialog.ChooseBackup(this,backups,preferredPath);
            if(choice==null){StatusText.Text=previousStatus;return;}
            StatusText.Text="正在还原 Steam 文件…";
            string? undoBackup=null;
            if(choice.Undo) undoBackup=await Task.Run(()=>SteamLibrary.UndoManaged(steam,dataDirectory,options.RestartSteam));
            else await Task.Run(()=>SteamLibrary.Restore(steam,choice.Path!,options.RestartSteam,dataDirectory));
            lastWriteBackup=string.IsNullOrEmpty(undoBackup)?null:(steam,undoBackup);
            await LoadAsync();
            StatusText.Text=choice.Undo?(string.IsNullOrEmpty(undoBackup)?"当前没有可撤销的写入。":"已撤销可恢复的统译写入。"):"已还原所选备份，并重新读取游戏库。";
        });
    }
    private async Task<bool> OnlineAsync(List<GameRow> targets,bool descriptions)
    {
        if(!descriptions)targets=targets.Where(r=>r.NeedsName).ToList();
        if(targets.Count==0){StatusText.Text=descriptions?"当前列表没有已勾选的游戏。":"所选游戏的名称均已处理。";return true;}
        bool finished=false;
        await WorkAsync(descriptions?"正在补全商店简介…":"正在联网查找中文名…",async ()=>{
            int completed=0,found=0;
            var token=cancellation!.Token;
            string phase=descriptions?"正在补全简介":"正在查找中文名";
            ReportCount(phase,0,targets.Count,0);
            DrawProgress();
            try{
                await suggestions.LookupBatchAsync(targets.Select(row=>row.Game).ToArray(),descriptions,options.Proxy,success=>{
                    if(success)Interlocked.Increment(ref found);
                    ReportCount(phase,Interlocked.Increment(ref completed),targets.Count,Volatile.Read(ref found));
                },token);
            }finally{
                await Task.Run(suggestions.Save);await RecalculateAsync(targets.Select(r=>r.Id).ToHashSet());
            }
            token.ThrowIfCancellationRequested();
            StatusText.Text=$"已查询 {completed} 款游戏，{(descriptions?"获得简介":"找到中文名")} {found} 项。";
            finished=true;
        },true);
        return finished;
    }
    private void ShowMore(object sender,RoutedEventArgs e)
    {
        FlushSearch();
        WarningsButton.Visibility=scanWarnings.Count>0?Visibility.Visible:Visibility.Collapsed;
        MoreButton.ContextMenu.PlacementTarget=MoreButton;
        MoreButton.ContextMenu.IsOpen=true;
    }
    private async void EnrichChecked(object sender,RoutedEventArgs e)=>
        await OnlineAsync(Visible().Where(row=>row.Picked).ToList(),true);
    private async void ImportRecords(object sender,RoutedEventArgs e)=>await ImportAsync();
    private async void ExportRows(object sender,RoutedEventArgs e)=>await ExportAsync();
    private void ShowWarnings(object sender,RoutedEventArgs e)=>
        StatusText.Text=string.Join("  ·  ",scanWarnings);
    private async Task ExportAsync()
    {
        var save=new SaveFileDialog{Title="导出游戏列表",Filter="CSV 表格 (*.csv)|*.csv",FileName="Steam 游戏库.csv"};
        if(save.ShowDialog(this)!=true)return;
        var export=Visible().ToArray();
        await WorkAsync("正在导出…",async ()=>{
            static string Cell(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
            using var file=new StreamWriter(save.FileName,false,new UTF8Encoding(true));
            await file.WriteLineAsync("AppID,当前名称,建议中文名,主收藏集,标签,状态");
            foreach(var row in export)await file.WriteLineAsync(string.Join(",",new[]{row.Id.ToString(),row.Game.Name,row.Name,row.Group,string.Join("、",row.Suggested.Tags),row.Status}.Select(Cell)));
            StatusText.Text=$"已导出 {export.Length} 款游戏。";
        });
    }
    private async Task ImportAsync()
    {
        var picker=new OpenFolderDialog{Title="选择旧版的 data 文件夹"};
        if(picker.ShowDialog(this)!=true)return;
        await WorkAsync("正在导入…",async ()=>{
            int count=await Task.Run(()=>suggestions.ImportLegacyFrom(picker.FolderName));
            await RecalculateAsync(rows.Select(r=>r.Id).ToHashSet());StatusText.Text=$"已导入 {count} 款游戏的旧版整理记录。";
        });
    }
}

