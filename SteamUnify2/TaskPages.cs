using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace SteamUnify;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? taskPage;
    private UIElement? previousFocus;

    internal Task<bool> ShowTaskPageAsync(FrameworkElement content,string title)
    {
        if(closingWhenDone)return Task.FromResult(false);
        if(taskPage!=null)throw new InvalidOperationException("请先完成当前页面。");
        taskPage=new(TaskCreationOptions.RunContinuationsAsynchronously);
        previousFocus=Keyboard.FocusedElement as UIElement;
        AutomationProperties.SetName(InPageHost,title);
        LibrarySurface.Visibility=Visibility.Collapsed;
        InPageHost.Content=content;
        InPageHost.Visibility=Visibility.Visible;
        content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        return taskPage.Task;
    }

    internal void CloseTaskPage(bool accepted)
    {
        if(taskPage is not { } page)return;
        taskPage=null;
        InPageHost.Visibility=Visibility.Collapsed;
        InPageHost.Content=null;
        LibrarySurface.Visibility=Visibility.Visible;
        if(previousFocus?.IsVisible==true && previousFocus.IsEnabled)previousFocus.Focus();
        else if(LibraryGrid.IsEnabled)LibraryGrid.Focus();
        previousFocus=null;
        page.TrySetResult(accepted);
    }
}
