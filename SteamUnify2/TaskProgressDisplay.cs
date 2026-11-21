using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace SteamUnify;

public partial class MainWindow
{
    private readonly DispatcherTimer progressTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};
    private readonly object progressLock=new();
    private (string? Phase,int Done,int Total,int Found) latestProgress;

    private void ReportProgress(string phase,int done,int total)=>ReportCount(phase,done,total,-1);

    private void ReportCount(string phase,int done,int total,int found)
    {
        lock(progressLock)
        {
            if(latestProgress.Phase==phase && latestProgress.Total==total && latestProgress.Done>done)return;
            latestProgress=(phase,done,total,found);
        }
    }

    private void DrawProgress()
    {
        if(!busy && candidateCancellation==null)
        {
            progressTimer.Stop();
            TaskProgress.Visibility=Visibility.Collapsed;
            return;
        }
        if(!progressTimer.IsEnabled)progressTimer.Start();
        (string? Phase,int Done,int Total,int Found) progress;
        lock(progressLock)progress=latestProgress;
        string label;
        int done,total;
        if(busy)
        {
            done=progress.Done;
            total=progress.Total;
            label=progress.Phase??StatusText.Text;
            if(progress.Phase!=null && !closingWhenDone)
                StatusText.Text=total>0?$"{label}  {done:N0} / {total:N0}"+(progress.Found>=0?$"，已找到 {progress.Found:N0} 项":""):label;
        }
        else
        {
            done=Volatile.Read(ref candidateDone);
            total=candidateTotal;
            label="后台补译";
        }
        TaskProgress.Visibility=Visibility.Visible;
        TaskProgress.IsIndeterminate=total<=0;
        TaskProgress.Maximum=Math.Max(1,total);
        TaskProgress.Value=Math.Clamp(done,0,TaskProgress.Maximum);
        AutomationProperties.SetName(TaskProgress,label);
    }
}
