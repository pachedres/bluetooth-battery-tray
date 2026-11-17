using Microsoft.Win32;
using static BluetoothBatteryTray.Interop.NativeMethods;

namespace BluetoothBatteryTray.Interop;

internal static class Taskbar
{
    /// <summary>
    /// Finds the primary taskbar and the x coordinate (physical pixels) where the
    /// notification area starts, i.e. where our overlay should end.
    /// </summary>
    public static bool TryGetLayout(out RECT taskbar, out int notifyAreaLeft)
    {
        taskbar = default;
        notifyAreaLeft = 0;

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out taskbar) || taskbar.Width <= 0)
            return false;

        notifyAreaLeft = taskbar.Right;

        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero
            && GetWindowRect(notify, out var n)
            && n.Width > 0
            && n.Left > taskbar.Left
            && n.Width < taskbar.Width / 2)
        {
            notifyAreaLeft = n.Left;
        }

        return true;
    }

    /// <summary>True if the taskbar uses the light theme (dark text).</summary>
    public static bool UsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    /// <summary>
    /// True when a full-screen app, game, video or presentation is in front on the primary monitor.
    /// (QUNS_BUSY is deliberately not used: Windows reports it briefly while ordinary apps start,
    /// which made the overlay blink.)
    /// </summary>
    public static bool IsFullScreenAppRunning()
    {
        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false; // desktop / taskbar

        if (!GetWindowRect(fg, out var window)) return false;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        if ((info.dwFlags & MONITORINFOF_PRIMARY) == 0) return false; // overlay only lives on the primary taskbar

        // Maximized windows only cover the work area; full-screen ones cover the whole monitor.
        var m = info.rcMonitor;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }
}
