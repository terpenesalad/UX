using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Horizon;

/// <summary>Friendly helpers over the raw Win32 calls for reading and moving other apps' windows.</summary>
internal static class Win
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow"
    };

    /// <summary>True for normal, top-level app windows that Horizon is allowed to move.</summary>
    public static bool IsManageable(IntPtr hwnd, HorizonConfig cfg)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return false;

        int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        bool hasFrame = (style & Native.WS_CAPTION) == Native.WS_CAPTION || (style & Native.WS_THICKFRAME) != 0;
        if (!hasFrame) return false;
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;

        if (ShellClasses.Contains(ClassName(hwnd))) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;

        string process = ProcessName(pid);
        return !cfg.IgnoreProcesses.Any(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The window's visible bounds (without the invisible resize border Windows 10/11 adds).</summary>
    public static Rectangle GetVisibleBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            return frame.ToRectangle();
        }

        Native.GetWindowRect(hwnd, out var rect);
        return rect.ToRectangle();
    }

    /// <summary>Moves/resizes a window so its *visible* edges land on <paramref name="target"/>.</summary>
    public static void SetVisibleBounds(IntPtr hwnd, Rectangle target)
    {
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        Native.GetWindowRect(hwnd, out var outer);
        var visible = GetVisibleBounds(hwnd);
        int left = visible.Left - outer.Left;
        int top = visible.Top - outer.Top;
        int right = outer.Right - visible.Right;
        int bottom = outer.Bottom - visible.Bottom;

        bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero,
            target.X - left, target.Y - top,
            target.Width + left + right, target.Height + top + bottom,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        if (!ok)
        {
            // Usually an app running as administrator: Windows blocks non-admin apps from moving it.
            Log.Write($"Could not move window '{Title(hwnd)}' (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public static void Activate(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessId(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    public static string ProcessName(IntPtr hwnd) => ProcessName(ProcessId(hwnd));

    private static string? ProcessPath(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null; // Elevated or protected process.
        }
    }

    /// <summary>A short, human name for the app: "Spotify", "Google Chrome", or the window title.</summary>
    public static string FriendlyAppName(IntPtr hwnd)
    {
        uint pid = ProcessId(hwnd);
        string? path = ProcessPath(pid);
        if (path != null)
        {
            try
            {
                string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
            catch
            {
                // Fall through.
            }
        }

        string name = ProcessName(pid);
        if (!string.IsNullOrEmpty(name)) return char.ToUpper(name[0]) + name[1..];
        return Title(hwnd);
    }

    /// <summary>The window's own icon, falling back to its exe's icon, then a generic one.</summary>
    public static Bitmap AppIcon(IntPtr hwnd, int size = 32)
    {
        Icon? icon = null;
        try
        {
            IntPtr hIcon = IntPtr.Zero;
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)Native.ICON_BIG, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 200, out hIcon);
            if (hIcon == IntPtr.Zero)
                Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)Native.ICON_SMALL2, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 200, out hIcon);
            if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
            if (hIcon != IntPtr.Zero) icon = (Icon)Icon.FromHandle(hIcon).Clone();

            if (icon == null)
            {
                string? path = ProcessPath(ProcessId(hwnd));
                if (path != null) icon = Icon.ExtractAssociatedIcon(path);
            }
        }
        catch
        {
            // Fall back below.
        }

        using var source = (icon ?? SystemIcons.Application).ToBitmap();
        return new Bitmap(source, size, size);
    }
}
