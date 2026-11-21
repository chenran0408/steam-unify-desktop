using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace SteamUnify;

internal sealed class TaskDialog : UserControl
{
    private readonly MainWindow owner;
    private readonly string pageTitle;
    private readonly TextBlock error = new() { Foreground=new SolidColorBrush(Color.FromRgb(151,52,38)),
        TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,6,0,0), Visibility=Visibility.Collapsed };
    private static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(246,247,249));
    private readonly DockPanel frame = new() { Margin = new Thickness(22,18,22,16) };
    private readonly WrapPanel footer = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel fields = new();
    private readonly TextBlock heading = new() { FontSize = 23, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
    private TaskDialog(MainWindow owner,string title)
    {
        this.owner=owner;
        pageTitle=title;
        HorizontalContentAlignment=HorizontalAlignment.Stretch;
        VerticalContentAlignment=VerticalAlignment.Stretch;
        Language=System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
        Content = frame;
        var actions=new Border { Child=footer, BorderBrush=(Brush)FindResource("Line"), BorderThickness=new Thickness(0,1,0,0),
            Padding=new Thickness(0,8,0,0), Margin=new Thickness(0,12,0,0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        frame.Children.Add(actions);
        AutomationProperties.SetAutomationId(error,"TaskError");
        AutomationProperties.SetLiveSetting(error,AutomationLiveSetting.Assertive);
        DockPanel.SetDock(error,Dock.Bottom);
        frame.Children.Add(error);
        heading.Text = title;
        DockPanel.SetDock(heading, Dock.Top);
        frame.Children.Add(heading);
    }
    private Task<bool> ShowAsync()=>owner.ShowTaskPageAsync(this,pageTitle);
    private void Finish(bool accepted)=>owner.CloseTaskPage(accepted);
    private void Error(string message,Control? input=null)
    {
        error.Text=message;
        error.Visibility=Visibility.Visible;
        input?.Focus();
    }
    private Border Sheet(UIElement child, Thickness padding) => new() { Child=child, Background=Brushes.White,
        BorderBrush=(Brush)FindResource("Line"), BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(5), Padding=padding };
    public void Form()
    {
        var sheet=Sheet(new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},new Thickness(15,7,15,12));
        fields.MaxWidth=730;
        fields.HorizontalAlignment=HorizontalAlignment.Stretch;
        frame.Children.Add(sheet);
    }
    public void Body(UIElement body) => frame.Children.Add(body);
    public void Field(UIElement control) => fields.Children.Add(control);
    public TextBlock Note(string text)
    {
        var note = new TextBlock {Text=text, Foreground=(Brush)FindResource("Muted"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,5,0,10)};
        fields.Children.Add(note);
        return note;
    }
    private void Label(string label,UIElement? action=null)
    {
        var line=new DockPanel { Margin=new Thickness(0,9,0,5) };
        if(action!=null) { DockPanel.SetDock(action,Dock.Right);line.Children.Add(action); }
        line.Children.Add(new TextBlock {Text=label, FontWeight=FontWeights.SemiBold, VerticalAlignment=VerticalAlignment.Center });
        Field(line);
    }
    public TextBox Input(string label,string text,UIElement? action=null)
    {
        Label(label,action);
        var box = new TextBox {Text=text};
        AutomationProperties.SetName(box,label); Field(box);
        return box;
    }
    public CheckBox Check(string label, bool value)
    {
        var control=new CheckBox {Content=label, IsChecked=value, Margin=new Thickness(0,9,0,4)};
        Field(control); return control;
    }
    public Button Action(string label, bool primary, Action action)
    {
        var button = new Button {Content=label, Margin=new Thickness(8,4,0,0), IsDefault=primary};
        if (primary) button.Style=(Style)FindResource("Primary");
        button.Click+=(_,_)=>action(); footer.Children.Add(button);
        return button;
    }
    public void Cancel(string text="取消") => Action(text,false,()=>Finish(false));
    public static async Task<bool> ConfirmRecommended(MainWindow owner,int games,int names,int groups,bool restart)
    {
        var d=new TaskDialog(owner,"退出 Steam 并整理");
        d.Note($"Steam 正在运行。退出后将整理 {games:N0} 款游戏，其中改名 {names:N0} 款、归类 {groups:N0} 款。");
        d.Note(restart?"写入前会自动备份，完成后会重新打开 Steam。":"写入前会自动备份，完成后可手动打开 Steam。");
        d.Form();
        d.Cancel("稍后再说");
        d.Action("退出 Steam 并应用",true,()=>d.Finish(true));
        return await d.ShowAsync();
    }
    public static async Task<bool> ConfirmAllGames(MainWindow owner,bool restart)
    {
        var dialog=new TaskDialog(owner,"创建「全部游戏」动态收藏");
        dialog.Note("Steam 正在运行。创建收藏前需要退出客户端。若正在游戏或下载，可以稍后再创建。");
        dialog.Note(restart?"创建完成后会重新打开 Steam。":"创建完成后可手动打开 Steam。");
        dialog.Form();
        dialog.Cancel("稍后再说");
        dialog.Action("退出 Steam 并创建",true,()=>dialog.Finish(true));
        return await dialog.ShowAsync();
    }
    public static async Task<DesktopOptions?> Settings(MainWindow owner, DesktopOptions options)
    {
        var d=new TaskDialog(owner,"设置");
        var root=d.Input("Steam 安装目录",options.Root);
        var browse = new Button {Content="选择文件夹…",HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,5,0,0)};
        browse.Click+=(_,_)=> {var picker=new OpenFolderDialog{Title="选择包含 steam.exe 的目录"}; if(picker.ShowDialog(owner)==true) root.Text=picker.FolderName;};
        d.Field(browse);
        var account=d.Input("Steam 账号目录编号（留空自动识别）",options.Account);
        var all=d.Check("显示全部缓存游戏，包括共享和曾浏览的条目",options.AllCached);
        var tools=d.Check("包含软件和工具",options.IncludeTools);
        var network=d.Check("自动联网查找新游戏的中文名",options.AutoLookup);
        var restart=d.Check("操作完成后，重新打开本次退出的 Steam",options.RestartSteam);
        var proxy=d.Input("网络代理（留空直连）",options.Proxy);
        d.Form(); d.Cancel();
        DesktopOptions? result=null;
        d.Action("保存并扫描",true,()=>{
            var id=account.Text.Trim();
            if(id.Length>0 && !id.All(char.IsAsciiDigit)) { d.Error("账号编号只能包含数字。",account);return; }
            var address=proxy.Text.Trim();
            if(address.Length>0 && (!Uri.TryCreate(address,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https" or "socks5"))) {d.Error("请填写完整代理地址，例如 http://127.0.0.1:7890。",proxy);return;}
            result=new(root.Text.Trim().Trim('"'),id,all.IsChecked==true,tools.IsChecked==true,restart.IsChecked==true,address,network.IsChecked==true);
            d.Finish(true);
        });
        return await d.ShowAsync()?result:null;
    }

    private sealed class PreviewColumn(string text, string selected, string idPrefix, string action,
        string? available = null, bool showSource = false, string? enabled = null) : DataGridColumn
    {
        protected override FrameworkElement GenerateElement(DataGridCell cell, object item)
        {
            var panel = new DockPanel { Margin = new Thickness(10,0,6,0), LastChildFill = true };
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            check.SetBinding(CheckBox.IsCheckedProperty,new Binding(selected) { Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(AutomationProperties.AutomationIdProperty,new Binding("Id") { StringFormat=idPrefix+"_{0}" });
            check.SetBinding(AutomationProperties.NameProperty,new Binding("Title") { StringFormat=action+" · {0}" });
            if (available != null)
                check.SetBinding(UIElement.VisibilityProperty,new Binding(available) { Converter=new BooleanToVisibilityConverter() });
            if (enabled != null)
                check.SetBinding(UIElement.IsEnabledProperty,new Binding(enabled));
            DockPanel.SetDock(check,Dock.Left);
            panel.Children.Add(check);
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            var labelStyle = new Style(typeof(TextBlock));
            labelStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Medium));
            var excluded = new DataTrigger { Binding=new Binding(selected), Value=false };
            excluded.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)Application.Current.FindResource("Muted")));
            excluded.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Normal));
            labelStyle.Triggers.Add(excluded);
            label.Style=labelStyle;
            label.SetBinding(TextBlock.TextProperty,new Binding(text));
            label.SetBinding(FrameworkElement.ToolTipProperty,new Binding(text));
            lines.Children.Add(label);
            if (showSource)
            {
                var source = new TextBlock { Text="机器翻译", FontSize=11, Foreground=(Brush)Application.Current.FindResource("Muted") };
                source.SetBinding(UIElement.VisibilityProperty,new Binding("Machine") { Converter=new BooleanToVisibilityConverter() });
                lines.Children.Add(source);
            }
            lines.MouseLeftButtonUp += (_, e) => {
                if (!check.IsVisible || !check.IsEnabled) return;
                check.Focus();
                check.SetCurrentValue(CheckBox.IsCheckedProperty, check.IsChecked == false);
                e.Handled = true;
            };
            panel.Children.Add(lines);
            return panel;
        }
        protected override FrameworkElement GenerateEditingElement(DataGridCell cell,object item) => GenerateElement(cell,item);
    }

    private sealed class BulkHeader : CheckBox
    {
        private readonly string selectHint;
        private readonly string clearHint;
        private bool updating, clearNext;
        public event Action<bool>? BulkChanged;

        public BulkHeader(string caption,string id,string selectHint,string clearHint)
        {
            Content=caption;
            this.selectHint=selectHint;
            this.clearHint=clearHint;
            AutomationProperties.SetAutomationId(this,id);
            Checked+=Toggle;
            Unchecked+=Toggle;
            void Toggle(object sender,RoutedEventArgs e)
            {
                if (!updating) BulkChanged?.Invoke(!clearNext);
            }
        }
        public void UpdateState(int available,int picked,int unpickedRegular)
        {
            clearNext=picked>0 && unpickedRegular==0;
            updating=true;
            try { IsChecked=picked==0 ? false : picked==available ? true : null; }
            finally { updating=false; }
            IsEnabled=picked>0 || unpickedRegular>0;
            string hint=clearNext?clearHint:selectHint;
            ToolTip=hint;
            AutomationProperties.SetName(this,$"{Content}，{hint}");
        }
    }

    public static async Task<List<ChangeLine>?> Preview(MainWindow owner,List<ChangeLine> edits,bool restart)
    {
        var dialog=new TaskDialog(owner,"预览更改");
        var layout=new DockPanel();
        bool hasNames=edits.Any(line=>line.Edit.Name!=null), hasGroups=edits.Any(line=>line.Edit.Collection!=null);
        var scope=new TextBlock { Text=string.Join("  ",new[] {
            hasNames ? "名称在本机的 Steam 账号间共享。" : null,
            hasGroups ? "收藏变更仅调整当前账号的统译收藏。" : null }.OfType<string>()),
            Foreground=(Brush)dialog.FindResource("Muted"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,8) };
        AutomationProperties.SetAutomationId(scope,"PreviewScope");
        DockPanel.SetDock(scope,Dock.Top);layout.Children.Add(scope);
        var summary=dialog.heading;
        AutomationProperties.SetAutomationId(summary,"PreviewSummary");
        AutomationProperties.SetLiveSetting(summary,AutomationLiveSetting.Polite);
        var machineHint = new TextBlock{Foreground=(Brush)dialog.FindResource("Muted"),FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        AutomationProperties.SetAutomationId(machineHint,"PreviewPendingMachine");
        DockPanel.SetDock(machineHint,Dock.Top);layout.Children.Add(machineHint);
        var note=new TextBlock{Text="写入前会创建备份。"+(restart?"如 Steam 正在运行，将先退出，完成后重新打开。":"如 Steam 正在运行，将先退出，完成后可手动打开。"),
            Foreground=(Brush)dialog.FindResource("Muted"),FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
        DockPanel.SetDock(note,Dock.Bottom);layout.Children.Add(note);
        var grid=new DataGrid{IsReadOnly=true,RowHeight=46,Background=Brushes.White,SelectionUnit=DataGridSelectionUnit.FullRow};
        var rowStyle=new Style(typeof(DataGridRow),dialog.TryFindResource(typeof(DataGridRow)) as Style);
        var uncheckedRow=new DataTrigger{Binding=new Binding("PickGame"),Value=false};
        uncheckedRow.Setters.Add(new Setter(DataGridRow.BackgroundProperty,Surface));
        rowStyle.Triggers.Add(uncheckedRow);
        grid.RowStyle=rowStyle;
        List<PreviewChoice> choices=[];
        bool batching=false, both=hasNames && hasGroups;
        void ChangeAll(bool pick,Action<PreviewChoice,bool> select)
        {
            batching=true;
            try { foreach(var choice in choices)select(choice,pick); }
            finally { batching=false; }
            Refresh();
        }
        var all=new BulkHeader(both?"当前游戏名称":hasNames?"写入名称":"统译收藏变更","PreviewAll",
            hasNames?"勾选所有常规更改，机翻需单独确认":"勾选所有收藏变更","清除所有勾选");
        var names=both?new BulkHeader("写入名称","PreviewAllNames","勾选常规名称，机翻需单独确认","清除所有名称勾选"):null;
        var groups=both?new BulkHeader("统译收藏变更","PreviewAllGroups","勾选所有收藏变更","清除所有收藏勾选"):null;
        grid.Columns.Add(new PreviewColumn("Title","PickGame","PreviewGame","包含游戏",enabled:"CanToggleGame"){ Header=both?(object)all:"当前游戏名称", MinWidth=125, Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        if(hasNames) grid.Columns.Add(new PreviewColumn("Name","PickName","PreviewName","应用名称","HasName",true){ Header=names??all, MinWidth=125, Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        if(hasGroups) grid.Columns.Add(new PreviewColumn("Collection","PickGroup","PreviewGroup","应用收藏集","HasGroup"){ Header=groups??all, MinWidth=155, Width=new DataGridLength(1.12,DataGridLengthUnitType.Star)});
        layout.Children.Add(dialog.Sheet(grid,new Thickness(0)));
        dialog.Body(layout); dialog.Cancel("返回");
        var apply=dialog.Action("应用到 Steam",true,()=>dialog.Finish(true));
        AutomationProperties.SetAutomationId(apply,"PreviewApply");
        int totalNames=edits.Count(line=>line.Edit.Name!=null), totalGroups=edits.Count(line=>line.Edit.Collection!=null);
        void Refresh()
        {
            if (batching) return;
            int games=0,pickedNames=0,pickedGroups=0,pending=0;
            foreach(var choice in choices)
            {
                if(choice.PickName||choice.PickGroup)games++;
                if(choice.PickName)pickedNames++;
                else if(choice.NeedsNameApproval)pending++;
                if(choice.PickGroup)pickedGroups++;
            }
            int unpickedNames=totalNames-pickedNames-pending,unpickedGroups=totalGroups-pickedGroups;
            all.UpdateState(totalNames+totalGroups,pickedNames+pickedGroups,unpickedNames+unpickedGroups);
            names?.UpdateState(totalNames,pickedNames,unpickedNames);
            groups?.UpdateState(totalGroups,pickedGroups,unpickedGroups);
            machineHint.Text=pending==0 ? "" : $"{pending} 个机翻译名需逐项勾选。";
            machineHint.Visibility=pending==0?Visibility.Collapsed:Visibility.Visible;
            summary.Text=$"将修改 {games} 款游戏  ·  {pickedNames} 个名称  ·  {pickedGroups} 项收藏集归属";
            apply.IsEnabled=games!=0;
        }
        choices=edits.Select(line=>new PreviewChoice(line,Refresh)).ToList();
        all.BulkChanged+=pick=>ChangeAll(pick,(choice,value)=>choice.PickGame=value);
        if(names!=null) names.BulkChanged+=pick=>ChangeAll(pick,(choice,value)=>choice.SetBulkName(value));
        if(groups!=null) groups.BulkChanged+=pick=>ChangeAll(pick,(choice,value)=>choice.PickGroup=value);
        grid.ItemsSource=choices; Refresh();
        return await dialog.ShowAsync()?choices.Select(c=>c.Selected).OfType<ChangeLine>().ToList():null;
    }

    public sealed record Recovery(bool Undo, string? Path);
    internal static string BackupLabel(string path)
    {
        string leaf=Path.GetFileName(path);
        if (leaf.Length==28 && leaf[19]=='-' && leaf[20..].All(char.IsAsciiHexDigit) &&
            DateTime.TryParseExact(leaf[..19],"yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture,DateTimeStyles.None,out var native))
            return native.ToString("yyyy年M月d日  HH:mm:ss.fff",CultureInfo.InvariantCulture);
        if (leaf.Length>=15 && DateTime.TryParseExact(leaf[..15],"yyyyMMdd-HHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var old))
        {
            string seq=leaf.Length>16 && int.TryParse(leaf[16..],out int n) ? $" · 第 {n+1} 份" : "";
            return old.ToString("yyyy年M月d日  HH:mm:ss",CultureInfo.InvariantCulture)+" · 旧版"+seq;
        }
        return Directory.Exists(path)?Directory.GetCreationTime(path).ToString("yyyy年M月d日  HH:mm:ss",CultureInfo.InvariantCulture):leaf;
    }
    public static async Task<Recovery?> ChooseBackup(MainWindow owner,List<string> backups,string? preferredPath=null)
    {
        var d=new TaskDialog(owner,"备份与还原");
        var layout=new DockPanel();
        var note=new TextBlock{Text="按备份时间整份还原。所选备份覆盖的文件都会回到当时的状态，之后对这些文件的修改也会一起回退。",
            Foreground=(Brush)d.FindResource("Muted"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        AutomationProperties.SetAutomationId(note,"BackupScope");
        DockPanel.SetDock(note,Dock.Top); layout.Children.Add(note);
        var items=backups.Select(path=>new {Path=path,Name=BackupLabel(path)+(string.Equals(path,preferredPath,StringComparison.OrdinalIgnoreCase)?"   ·   本次操作":"")}).ToList();
        int preferred=items.FindIndex(item=>string.Equals(item.Path,preferredPath,StringComparison.OrdinalIgnoreCase));
        var list=new ListBox{ItemsSource=items,DisplayMemberPath="Name",SelectedValuePath="Path",
            Background=Brushes.White,BorderThickness=new Thickness(0),SelectedIndex=preferred>=0?preferred:items.Count>0?0:-1};
        var itemStyle=new Style(typeof(ListBoxItem),d.TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,7,10,7)));
        list.ItemContainerStyle=itemStyle;
        list.Loaded+=(_,_)=>{if(list.SelectedItem!=null)list.ScrollIntoView(list.SelectedItem);};
        AutomationProperties.SetAutomationId(list,"BackupList");
        AutomationProperties.SetName(list,"写入前的备份");
        var undoButton=new Button {Content="撤销历次整理",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,12,0)};
        var undoBar=new DockPanel {LastChildFill=true,Margin=new Thickness(0,12,0,0)};
        DockPanel.SetDock(undoButton,Dock.Left);undoBar.Children.Add(undoButton);
        undoBar.Children.Add(new TextBlock{Text="恢复统译改过的名称，并移除当前账号由统译管理的收藏。",TextWrapping=TextWrapping.Wrap,
            FontSize=13,Foreground=(Brush)d.FindResource("Muted"),VerticalAlignment=VerticalAlignment.Center});
        DockPanel.SetDock(undoBar,Dock.Bottom);layout.Children.Add(undoBar);
        if(backups.Count==0)layout.Children.Add(new TextBlock{Text="当前账号还没有备份。首次应用更改时会自动创建。",TextWrapping=TextWrapping.Wrap});
        else layout.Children.Add(d.Sheet(list,new Thickness(3)));
        d.Body(layout);
        bool undo=false;
        undoButton.Click+=(_,_)=>{undo=true;d.Finish(true);};
        d.Cancel("关闭");
        var restore=d.Action("还原所选备份",true,()=>{
            if(list.SelectedValue is string){undo=false;d.Finish(true);}
        });
        restore.IsEnabled=backups.Count>0;
        while(await d.ShowAsync())
        {
            string? path=list.SelectedValue as string;
            var confirm=new TaskDialog(owner,undo?"撤销历次整理":"确认还原");
            confirm.Note(undo?"恢复本机由统译改过的游戏名称，并移除当前账号由统译管理的收藏集。游戏名称由本机 Steam 账号共享。":
                $"将整份还原 {BackupLabel(path!)} 的备份。备份中的 Steam 文件会被覆盖。如果包含名称或收藏文件，备份后对这些内容的修改也会回退。");
            confirm.Form();
            confirm.Cancel("返回");
            var approve=confirm.Action(undo?"确认撤销":"确认还原",false,()=>confirm.Finish(true));
            AutomationProperties.SetAutomationId(approve,"ConfirmRecovery");
            if(await confirm.ShowAsync())return new Recovery(undo,path);
            if(!owner.IsVisible)return null;
        }
        return null;
    }

    public sealed record ManualEdit(bool Clear,string? Name,string? Group,bool Continue=false);

    private CheckBox PinAction(string key,bool pinned,string? skipCaption,Action skip,out Button? skipButton)
    {
        var bar=new DockPanel{Margin=new Thickness(0,4,0,2)};
        skipButton=null;
        if(skipCaption!=null)
        {
            var button=QuietButton(skipCaption);
            skipButton=button;
            AutomationProperties.SetAutomationId(button,"Skip"+key);
            button.Click+=(_,_)=>skip();
            DockPanel.SetDock(button,Dock.Right);bar.Children.Add(button);
        }
        var pin=new CheckBox{IsChecked=pinned,VerticalAlignment=VerticalAlignment.Center,MinHeight=28};
        AutomationProperties.SetAutomationId(pin,"Pin"+key);
        bar.Children.Add(pin);Field(bar);
        return pin;
    }

    private static Button QuietButton(string caption) => new() { Content=caption, Padding=new Thickness(7,2,7,2),
        MinHeight=28, Background=Brushes.Transparent, BorderBrush=Brushes.Transparent, HorizontalAlignment=HorizontalAlignment.Left };

    private static void ShowFieldChoice(CheckBox pin,Button? skip,bool empty,string useCaption,string skippedCaption)
    {
        bool skipped=empty && pin.IsChecked==true;
        pin.Content=skipped?skippedCaption:useCaption;
        pin.Visibility=empty && !skipped && skip!=null?Visibility.Collapsed:Visibility.Visible;
        if(skip!=null) skip.Visibility=skipped?Visibility.Collapsed:Visibility.Visible;
    }

    public static async Task<ManualEdit?> Edit(MainWindow owner,List<GameRow> rows,bool canContinue=false)
    {
        var one=rows.Count==1?rows[0]:null;
        var d=new TaskDialog(owner,one==null?$"批量编辑 {rows.Count} 款游戏":"编辑游戏");
        if(one!=null)
        {
            d.heading.Text=one.Game.Name;
            d.heading.TextTrimming=TextTrimming.CharacterEllipsis;
            d.heading.MaxHeight=70;
            d.heading.ToolTip=one.Game.Name;
        }
        else d.Note($"选择要为这 {rows.Count} 款游戏使用的统译收藏。");
        if(one!=null && one.Suggested.NeedsReview)
        {
            var reasons=new List<string>();
            if(one.MachineReview) reasons.Add("核对机器译名");
            if(one.NeedsName) reasons.Add("补充中文名称");
            if(one.NeedsGroup) reasons.Add("选择收藏集");
            if(reasons.Count>0) d.Note("待确认 · "+string.Join("、",reasons));
        }
        TextBox? name=null; CheckBox? pinName=null;
        Button? acceptMachine=null;
        ManualEdit? result=null;
        if(one!=null){
            if (one.MachineReview)
            {
                acceptMachine=new Button {
                    Content=canContinue ? "采用译名并查看下一款" : "仅采用此译名",
                    Padding=new Thickness(10,2,10,2),MinHeight=28,
                    ToolTip=one.NeedsGroup ? "采用当前机器译名，收藏集留待下次确认" : "采用当前机器译名"
                };
                AutomationProperties.SetAutomationId(acceptMachine,"AcceptMachineName");
                acceptMachine.Click+=(_,_)=>{
                    result=new(false,one.Name,one.Suggested.GroupLocked ? one.Group : null,canContinue);
                    d.Finish(true);
                };
            }
            name=d.Input(one.MachineReview?"中文名称 · 机器翻译":"中文名称",one.Name,acceptMachine);
            AutomationProperties.SetAutomationId(name,"NameInput");
            pinName=d.PinAction("Name",one.Suggested.NameLocked,
                one.MachineReview || one.NeedsName ? "跳过改名" : null,()=>{
                    name.Text=""; pinName!.IsChecked=true;pinName.Focus();
                },out var skipName);
            void NameCaption() => ShowFieldChoice(pinName,skipName,name.Text.Trim().Length==0,
                one.MachineReview && name.Text.Trim()==one.Name?"保存时采用此机器译名":"保存时使用此名称","已选择跳过改名");
            NameCaption();
            name.TextChanged+=(_,_)=>{pinName.IsChecked=true;NameCaption();};
            pinName.Checked+=(_,_)=>NameCaption();
            pinName.Unchecked+=(_,_)=>NameCaption();
        }
        d.Label("统译收藏集");
        var group=new ComboBox{IsEditable=true,ItemsSource=Suggestions.Groups,Text=one?.Group??"",Padding=new Thickness(6,4,6,4)};
        AutomationProperties.SetName(group,"主收藏集"); AutomationProperties.SetAutomationId(group,"GroupInput");d.Field(group);
        CheckBox? pinGroup=null;
        pinGroup=d.PinAction("Group",one?.Suggested.GroupLocked==true,
            one==null || one.NeedsGroup ? "跳过分类" : null,()=>{
                group.SelectedIndex=-1;group.Text="";pinGroup!.IsChecked=true;pinGroup.Focus();
            },out var skipGroup);
        void GroupCaption() => ShowFieldChoice(pinGroup,skipGroup,group.Text.Trim().Length==0,"保存时使用此收藏集","已选择跳过分类");
        GroupCaption();
        group.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>{
            if(d.IsLoaded && group.IsKeyboardFocusWithin) pinGroup.IsChecked=true;
            GroupCaption();
        }));
        group.SelectionChanged+=(_,_)=>{if(d.IsLoaded)pinGroup.IsChecked=true;};
        pinGroup.Checked+=(_,_)=>GroupCaption();
        pinGroup.Unchecked+=(_,_)=>GroupCaption();
        if (acceptMachine!=null)
        {
            // Keep other in-progress edits for the regular Save actions.
            void UpdateShortcut() => acceptMachine.IsEnabled=name!.Text.Trim()==one!.Name &&
                group.Text.Trim()==one.Group && (pinGroup.IsChecked==true)==one.Suggested.GroupLocked;
            name!.TextChanged+=(_,_)=>UpdateShortcut();
            group.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>UpdateShortcut()));
            pinGroup.Checked+=(_,_)=>UpdateShortcut();
            pinGroup.Unchecked+=(_,_)=>UpdateShortcut();
            UpdateShortcut();
        }
        if(one!=null && one.Suggested.Tags.Length>0) d.Note("参考标签 · "+string.Join("、",one.Suggested.Tags.Take(6)));
        d.Field(new Border {Height=1,Background=(Brush)d.FindResource("Line"),Margin=new Thickness(0,9,0,7)});
        var formActions=new DockPanel();
        var reset=QuietButton("恢复自动建议");
        reset.Click+=(_,_)=>{result=new(true,null,null);d.Finish(true);};
        DockPanel.SetDock(reset,Dock.Right);formActions.Children.Add(reset);
        const string savedLocally="保存到整理方案，应用后写入 Steam。";
        var saveHint=new TextBlock {Text=savedLocally,FontSize=13,TextWrapping=TextWrapping.Wrap,
            VerticalAlignment=VerticalAlignment.Center,Foreground=(Brush)d.FindResource("Muted")};
        formActions.Children.Add(saveHint);
        d.Field(formActions);
        d.Form();
        d.Cancel();
        void Save(bool next)
        {
            string? updatedName=pinName?.IsChecked==true?name!.Text.Trim():null;
            string? updatedGroup=pinGroup.IsChecked==true?group.Text.Trim():null;
            if ((updatedName!=null && updatedName.Any(char.IsControl)) || (updatedGroup!=null && updatedGroup.Any(char.IsControl))) {d.Error("名称或收藏集含有控制字符。");return;}
            result=new(false,updatedName,updatedGroup,next);d.Finish(true);
        }
        var save=d.Action("保存",true,()=>Save(false)); AutomationProperties.SetAutomationId(save,"EditSave");
        if(one!=null && canContinue)
        {
            var next=d.Action("保存并查看下一款",false,()=>Save(true));
            AutomationProperties.SetAutomationId(next,"EditContinue");
            if(one.NeedsName || one.MachineReview || one.NeedsGroup)
            {
                void PendingNote()
                {
                    var fields=new List<string>();
                    if((one.NeedsName||one.MachineReview) && pinName?.IsChecked!=true) fields.Add("译名");
                    if(one.NeedsGroup && pinGroup.IsChecked!=true) fields.Add("主收藏集");
                    saveHint.Text=fields.Count>0 ? "保存后仍待确认 · "+string.Join("、",fields) : savedLocally;
                }
                pinName!.Checked+=(_,_)=>PendingNote();pinName.Unchecked+=(_,_)=>PendingNote();
                pinGroup.Checked+=(_,_)=>PendingNote();pinGroup.Unchecked+=(_,_)=>PendingNote();
                PendingNote();
            }
        }
        return await d.ShowAsync()?result:null;
    }
}

