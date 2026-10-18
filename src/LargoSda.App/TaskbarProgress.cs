using System.Runtime.InteropServices;

namespace LargoSda.App;

/// <summary>Progress bar on the app's taskbar button (Windows ITaskbarList3): shows the update download without any window.</summary>
public static class TaskbarProgress
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListCom { }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }

    private const int NoProgress = 0, Indeterminate = 1, Normal = 2;

    /// <summary>fraction 0..1 — show it; null — remove the bar; negative — "in progress, unknown amount".</summary>
    public static void Set(IntPtr hwnd, double? fraction)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        try
        {
            var list = (ITaskbarList3)new TaskbarListCom();
            list.HrInit();
            if (fraction is null) list.SetProgressState(hwnd, NoProgress);
            else if (fraction < 0) list.SetProgressState(hwnd, Indeterminate);
            else
            {
                list.SetProgressState(hwnd, Normal);
                list.SetProgressValue(hwnd, (ulong)(Math.Clamp(fraction.Value, 0, 1) * 1000), 1000);
            }
        }
        catch
        {
            // the taskbar button is a nicety: never break the update because of it
        }
    }
}
