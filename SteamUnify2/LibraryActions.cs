using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamUnify;

public partial class MainWindow
{
    private int? checkAnchor;
    private bool CanTranslate(GameRow row)=>row.NeedsName && !pendingCandidates.Contains(row.Id) || row.HasNameChange && !row.MachineReview;

    private void ToggleFilter(object sender,RoutedEventArgs e)
    {
        FilterPanel.Visibility=FilterPanel.IsVisible?Visibility.Collapsed:Visibility.Visible;
    }

    private async void UseLibraryScope(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var updated=options with { AllCached=false };
        try{updated.Save(dataDirectory);options=updated;}
        catch(Exception ex){StatusText.Text="设置保存失败。"+ex.Message;return;}
        await ScanAsync();
    }

    private void SetVisiblePicks(Func<GameRow,bool> choose)
    {
        FlushSearch();
        foreach(var row in Visible())row.Picked=choose(row);
        checkAnchor=null;
        UpdateSelection();
    }

    private void ChooseSelection(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var menu=new ContextMenu{PlacementTarget=BatchSelectButton,Placement=PlacementMode.Top};
        void Add(string caption,Func<GameRow,bool> choose)
        {
            var item=new MenuItem{Header=caption};
            item.Click+=(_,_)=>SetVisiblePicks(choose);
            menu.Items.Add(item);
        }
        Add("勾选当前结果",_=>true);
        Add("取消勾选当前结果",_=>false);
        Add("反选当前结果",row=>!row.Picked);
        menu.Items.Add(new Separator());
        Add("只勾选可分类的游戏",row=>row.HasGroupChange);
        Add("只勾选可翻译的游戏",CanTranslate);
        menu.IsOpen=true;
    }

    private void GridSelectionKeys(object sender,KeyEventArgs e)
    {
        if(!busy && e.Key==Key.Space && Keyboard.Modifiers==ModifierKeys.None &&
            Keyboard.FocusedElement is not ButtonBase && LibraryGrid.CurrentCell.Item is GameRow focused)
        {
            focused.Picked=!focused.Picked;
            checkAnchor=focused.Id;
            UpdateSelection();
            e.Handled=true;
            return;
        }
        if(!busy && e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.None && Keyboard.FocusedElement is not ButtonBase &&
            LibraryGrid.CurrentCell.Item is GameRow current)
        {
            e.Handled=true;
            _=EditRowsAsync([current]);
            return;
        }
        if(busy || e.Key!=Key.A)return;
        var modifiers=Keyboard.Modifiers;
        if(modifiers!=ModifierKeys.Control && modifiers!=(ModifierKeys.Control|ModifierKeys.Shift))return;
        e.Handled=true;
        SetVisiblePicks(_=>modifiers==ModifierKeys.Control);
    }

    private void PickAnchor(object sender,RoutedEventArgs e)
    {
        if(sender is CheckBox {DataContext:GameRow row})checkAnchor=row.Id;
    }
    private void PickGameCell(object sender,MouseButtonEventArgs e)
    {
        if(busy || sender is not DataGridCell { DataContext:GameRow row } cell)return;
        var keys=Keyboard.Modifiers;
        for(var element=e.OriginalSource as DependencyObject;element!=null && element!=cell;
            element=element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if(element is CheckBox || element is ButtonBase && keys==ModifierKeys.None)return;
        }
        if((keys&~(ModifierKeys.Shift|ModifierKeys.Control))!=0)return;
        FlushSearch();
        if(view?.Contains(row)!=true)return;

        bool extend=keys.HasFlag(ModifierKeys.Shift);
        bool toggle=keys.HasFlag(ModifierKeys.Control);
        bool rangePicked=extend && PickRange(row,additive:toggle,followAnchor:cell.Column==LibraryGrid.Columns[0]);
        if(!rangePicked)
        {
            if(toggle || cell.Column==LibraryGrid.Columns[0])row.Picked=!row.Picked;
            else foreach(var item in Visible())item.Picked=item==row;
            checkAnchor=row.Id;
        }
        LibraryGrid.CurrentCell=new DataGridCellInfo(row,cell.Column);
        cell.Focus();
        UpdateSelection();
        e.Handled=true;
    }

    private bool PickRange(GameRow target,bool additive,bool followAnchor)
    {
        if(checkAnchor==null)return false;
        var ordered=Visible().ToList();
        int last=ordered.IndexOf(target);
        int first=ordered.FindIndex(row=>row.Id==checkAnchor);
        if(first<0 || last<0)return false;
        bool state=!followAnchor || ordered[first].Picked;
        int low=Math.Min(first,last),high=Math.Max(first,last);
        if(!additive && !followAnchor)
            for(int i=0;i<ordered.Count;i++)ordered[i].Picked=i>=low && i<=high;
        else
            for(int i=low;i<=high;i++)ordered[i].Picked=state;
        return true;
    }

    private void RangePick(object sender,MouseButtonEventArgs e)
    {
        if(busy || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || checkAnchor==null ||
            sender is not CheckBox {DataContext:GameRow target})return;
        FlushSearch();
        if(!PickRange(target,additive:true,followAnchor:true))return;
        e.Handled=true;
        UpdateSelection();
    }

    private async void ClassifyChecked(object sender,RoutedEventArgs e)=>await ApplyFieldAsync(false);
    private async void TranslateChecked(object sender,RoutedEventArgs e)=>await ApplyFieldAsync(true);

    private static List<ChangeLine> FieldPlan(IEnumerable<GameRow> games,bool translate)
    {
        var plan=new List<ChangeLine>();
        foreach(var row in games)
        {
            if(translate && row.HasNameChange && !row.MachineReview)plan.Add(new(row,new(row.Id,row.Name,null)));
            if(!translate && row.HasGroupChange)plan.Add(new(row,new(row.Id,null,row.Group)));
        }
        return plan;
    }

    private static RecommendedEdit[] Baseline(IEnumerable<ChangeLine> plan)=>
        plan.Select(line=>new RecommendedEdit(line.Edit,line.Row.Game.Name,line.Row.CurrentGroups)).ToArray();

    private async Task ApplyFieldAsync(bool translate)
    {
        if(busy || location is not { } steam)return;
        FlushSearch();
        var selected=Visible().Where(row=>row.Picked).ToList();
        CacheGame[] fallback=[];
        if(translate)
        {
            var missing=selected.Where(row=>row.NeedsName && !pendingCandidates.Contains(row.Id)).ToList();
            if(missing.Count>0)
            {
                var pending=await FindOfficialNamesAsync(missing);
                if(pending==null || closingWhenDone)return;
                fallback=pending;
            }
        }
        int machineNames=translate?selected.Count(row=>row.MachineReview):0;
        var remainder=machineNames>0?$"  ·  {machineNames:N0} 个机翻译名待核对":"";
        var plan=FieldPlan(selected,translate);
        if(plan.Count==0)
        {
            StatusText.Text=(translate?"没有可直接写入的中文名":"当前勾选的游戏没有收藏集变更")+remainder;
            await QueueMachineCandidatesAsync(fallback);
            return;
        }
        var ids=selected.Select(row=>row.Id).ToHashSet();
        var shown=Baseline(plan);
        var restart=options.RestartSteam;
        bool autoAccount=string.IsNullOrWhiteSpace(options.Account);
        IReadOnlyList<RecommendedEdit> ReadFreshPlan()
        {
            var fresh=ReadLibrary(ids);
            if(fresh.Location!=steam)throw new StaleRecommendationException("当前 Steam 游戏库已更换。");
            return Baseline(FieldPlan(fresh.Rows,translate));
        }
        void ConfirmShutdown()
        {
            if(!Dispatcher.Invoke(()=>TaskDialog.ConfirmRecommended(this,plan.Count,translate?plan.Count:0,translate?0:plan.Count,restart)).GetAwaiter().GetResult())
                throw new OperationCanceledException();
        }
        if(await WriteApprovedAsync(steam,plan,()=>SteamLibrary.ApplyRecommended(steam,dataDirectory,restart,shown,autoAccount,ConfirmShutdown,ReadFreshPlan,ReportProgress),remainder))
            await QueueMachineCandidatesAsync(fallback);
    }
}
