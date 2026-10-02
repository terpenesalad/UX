namespace Horizon;

/// <summary>
/// Hides the desktop icons for a clean background while KAMI UX runs, and shows them again on quit.
/// Nothing is changed on disk or in settings — Windows itself brings them back if Explorer restarts.
/// </summary>
internal static class DesktopIcons
{
    public static void Hide() => SetVisible(false);

    public static void Show() => SetVisible(true);

    private static void SetVisible(bool visible)
    {
        try
        {
            var list = FindIconList();
            if (list != IntPtr.Zero) Native.ShowWindow(list, visible ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
        }
        catch (Exception ex)
        {
            Log.Write("Could not change desktop icons: " + ex.Message);
        }
    }

    /// <summary>The icon list lives in SHELLDLL_DefView, under Progman or one of the WorkerW windows.</summary>
    private static IntPtr FindIconList()
    {
        IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            Native.EnumWindows((top, _) =>
            {
                var dv = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero) return true;
                defView = dv;
                return false;
            }, IntPtr.Zero);
        }

        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }
}
