using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamUnify;

internal static class WindowAppearance
{
    public static void MatchCaption(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            // DWM uses COLORREF (BGR). Keep native resize, snap and caption buttons.
            int caption = 0x00F2EFED;
            int text = 0x0029211F;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
