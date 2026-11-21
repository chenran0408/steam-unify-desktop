using System.Collections.Concurrent;
using System.Windows;

namespace SteamUnify;

public partial class MainWindow
{
    private CancellationTokenSource? candidateCancellation;
    private readonly HashSet<int> pendingCandidates=[];
    private readonly Dictionary<int,CacheGame> queuedCandidates=[];
    private readonly ConcurrentQueue<int> completedNames=new();
    private int candidateTotal,candidateDone;
    private sealed record CandidateReceipt(SteamLocation? Location,HashSet<int> Resolved,bool Stopped,string? Error);
    private CandidateReceipt? candidateReceipt;
    private bool candidateReceiptShown,reviewingCandidates;

    private async Task<CacheGame[]?> FindOfficialNamesAsync(List<GameRow> missing)
    {
        CacheGame[]? unresolved=null;
        await WorkAsync("正在查找官方中文名…",async ()=>{
            int done=0,found=0;
            var token=cancellation!.Token;
            const string phase="正在查找官方中文名";
            ReportCount(phase,0,missing.Count,0);
            DrawProgress();
            CacheGame[] result;
            try
            {
                result=await suggestions.LookupOfficialBatchAsync(missing.Select(r=>r.Game).ToArray(),options.Proxy,ok=>{
                    if(ok)Interlocked.Increment(ref found);
                    ReportCount(phase,Interlocked.Increment(ref done),missing.Count,Volatile.Read(ref found));
                },token);
            }
            finally
            {
                await Task.Run(suggestions.Save);
                await RecalculateAsync(missing.Select(r=>r.Id).ToHashSet());
            }
            token.ThrowIfCancellationRequested();
            unresolved=result;
        },true);
        return unresolved;
    }

    private void UpdateCandidateControls()
    {
        bool background=candidateCancellation!=null;
        if(candidateReceipt is { } receipt && (receipt.Location!=location || busy && candidateReceiptShown && !reviewingCandidates))
        {
            candidateReceipt=null;
            candidateReceiptShown=false;
        }
        CandidateReviewButton.Visibility=Visibility.Collapsed;
        if(background)
        {
            CandidateStatus.Text=$"后台补译 {Volatile.Read(ref candidateDone):N0} / {candidateTotal:N0}";
            CandidateStatus.ToolTip=null;
        }
        else if(!busy && !closingWhenDone && candidateReceipt is { } result)
        {
            int count=PendingCandidateNames(result).Count();
            string? detail=count>0?$"{count:N0} 个译名待核对":null;
            var summary=result.Error!=null?"后台补译未完成"+(detail==null?"":" · "+detail):
                result.Stopped?"后台补译已停止"+(detail==null?"":" · "+detail):
                detail??"没有找到可用译名";
            if(count>0)
            {
                CandidateReviewButton.Content=summary;
                CandidateReviewButton.ToolTip=result.Error??"查看本批机翻译名";
                CandidateReviewButton.Visibility=Visibility.Visible;
                CandidateStatus.Text="";
                CandidateStatus.ToolTip=null;
            }
            else
            {
                CandidateStatus.Text=summary;
                CandidateStatus.ToolTip=result.Error;
            }
            candidateReceiptShown=true;
        }
        else
        {
            CandidateStatus.Text="";
            CandidateStatus.ToolTip=null;
        }
        CandidateStatus.Visibility=CandidateReviewButton.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        var stoppable=cancellation??candidateCancellation;
        StopButton.Visibility=stoppable==null?Visibility.Collapsed:Visibility.Visible;
        StopButton.IsEnabled=stoppable?.IsCancellationRequested==false;
        StopButton.Content=cancellation!=null?"停止":"停止补译";
    }

    private IEnumerable<GameRow> PendingCandidateNames(CandidateReceipt receipt) =>
        rows.Where(row=>receipt.Resolved.Contains(row.Id) && row.MachineReview && row.HasNameChange);

    private async void ReviewCandidateNames(object sender,RoutedEventArgs e)
    {
        if(busy || reviewingCandidates || candidateReceipt is not { Location: { } steam } result || steam!=location)return;
        var edits=PendingCandidateNames(result).Select(row=>new ChangeLine(row,new(row.Id,row.Name,null))).ToList();
        reviewingCandidates=true;
        try { await PreviewAndApplyAsync(steam,edits); }
        finally
        {
            reviewingCandidates=false;
            if(candidateReceipt==result && !result.Stopped && result.Error==null && !PendingCandidateNames(result).Any())
                candidateReceipt=null;
            UpdateCandidateControls();
        }
    }

    private void RefreshCandidateNames()
    {
        if(busy || completedNames.IsEmpty || library==null)return;
        var changed=new HashSet<int>();
        while(completedNames.TryDequeue(out int id))changed.Add(id);
        foreach(var row in rows)
            if(changed.Contains(row.Id))row.Refresh(suggestions.Recommend(row.Game,library.TagNames,row.OriginalName));
        Filter();
    }

    private async Task QueueMachineCandidatesAsync(CacheGame[] games)
    {
        if(closingWhenDone || games.Length==0 || candidateCancellation?.IsCancellationRequested==true)return;
        foreach(var game in games)
            if(pendingCandidates.Add(game.AppId)){queuedCandidates.Add(game.AppId,game);candidateTotal++;}
        if(candidateCancellation!=null || queuedCandidates.Count==0)return;

        using var stop=new CancellationTokenSource();
        var origin=location;
        candidateReceipt=null;
        candidateReceiptShown=false;
        candidateCancellation=stop;
        var resolved=new ConcurrentDictionary<int,byte>();
        string? error=null;
        UpdateControls();
        try
        {
            while(queuedCandidates.Count>0)
            {
                var batch=queuedCandidates.Values.ToArray();
                queuedCandidates.Clear();
                try
                {
                    await suggestions.LookupMachineBatchAsync(batch,options.Proxy,(game,ok)=>{
                        if(ok)
                        {
                            completedNames.Enqueue(game.AppId);
                            resolved.TryAdd(game.AppId,0);
                        }
                        Interlocked.Increment(ref candidateDone);
                    },stop.Token);
                }
                finally {await Task.Run(suggestions.Save);}
                foreach(var game in batch)pendingCandidates.Remove(game.AppId);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) {}
        catch(Exception ex){error=ex.Message;}
        finally
        {
            candidateCancellation=null;
            queuedCandidates.Clear();pendingCandidates.Clear();
            candidateDone=candidateTotal=0;
            RefreshCandidateNames();
            candidateReceipt=new(origin,resolved.Keys.ToHashSet(),stop.IsCancellationRequested,error);
            UpdateControls();
            if(closingWhenDone && !busy)Close();
        }
    }
}
