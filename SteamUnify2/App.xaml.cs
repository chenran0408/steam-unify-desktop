using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
namespace SteamUnify;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SteamUnify.Desktop.v2", out bool first);
        if (!first)
        {
            ActivateOpenWindow();
            Shutdown();
            return;
        }
        try
        {
            string data = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(data);
            var window = new MainWindow(data);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Steam 统译启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static void ActivateOpenWindow()
    {
        using var self=Process.GetCurrentProcess();
        foreach(var other in Process.GetProcessesByName(self.ProcessName))
        {
            using(other)
            {
                if(other.Id==self.Id)continue;
                try
                {
                    if(other.SessionId!=self.SessionId || other.MainWindowTitle!="Steam 统译")continue;
                    var window=other.MainWindowHandle;
                    if(IsIconic(window))ShowWindowAsync(window,9);
                    SetForegroundWindow(window);
                    return;
                }
                catch(InvalidOperationException) when(other.HasExited) { }
            }
        }
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
