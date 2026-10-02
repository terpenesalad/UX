namespace Horizon;

/// <summary>
/// Hides the desktop icons for a clean background while KAMI UX runs, and shows them again on quit.
/// Nothing is changed on disk or in settings — Windows itself brings them back if Explorer restarts.
/// </summary>
internal static class DesktopIcons
{
    private static bool _hiddenByUs;

    /// <summary>Hides the icons (if they're showing). Remembers that it was us, so we only undo our own change.</summary>
    public static void Hide()
    {
        try
        {
            var list = FindIconList();
            if (list == IntPtr.Zero || !Native.IsWindowVisible(list)) return; // already hidden (perhaps by you)
            Native.ShowWindow(list, Native.SW_HIDE);
            _hiddenByUs = true;
        }
        catch (Exception ex)
        {
            Log.Write("Could not hide desktop icons: " + ex.Message);
        }
    }

    /// <summary>Shows the icons again — only if KAMI UX hid them.</summary>
    public static void Show()
    {
        if (!_hiddenByUs) return;
        try
        {
            var list = FindIconList();
            if (list != IntPtr.Zero) Native.ShowWindow(list, Native.SW_SHOWNOACTIVATE);
            _hiddenByUs = false;
        }
        catch (Exception ex)
        {
            Log.Write("Could not show desktop icons: " + ex.Message);
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
