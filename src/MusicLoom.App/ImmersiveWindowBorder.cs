using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace MusicLoom.App;

/// <summary>
/// The one-pixel line at the top of an inactive window belongs to DWM's
/// non-client frame, not the XAML TitleBar border. Windows 11 (22000+)
/// permits suppressing it without disabling the resize frame. Restore
/// the user's ordinary system border outside immersive mode.
/// </summary>
internal static class ImmersiveWindowBorder
{
    private const int DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref uint value, int size);

    internal static bool TrySetImmersive(Window window, bool immersive)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            uint color = immersive ? DwmColorNone : DwmColorDefault;
            return hwnd != 0
                && DwmSetWindowAttribute(hwnd, DwmwaBorderColor,
                    ref color, sizeof(uint)) >= 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
