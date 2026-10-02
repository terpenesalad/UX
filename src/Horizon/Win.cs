using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Horizon;

/// <summary>Friendly helpers over the raw Win32 calls for reading and moving other apps' windows.</summary>
internal static class Win
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland"
    };

    /// <summary>True for normal, top-level app windows that Horizon is allowed to move.</summary>
    public static bool IsManageable(IntPtr hwnd, HorizonConfig cfg)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return false;
        if (IsCloaked(hwnd)) return false;

        int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        bool hasFrame = (style & Native.WS_CAPTION) == Native.WS_CAPTION || (style & Native.WS_THICKFRAME) != 0;
        if (!hasFrame) return false;
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;

        if (ShellClasses.Contains(ClassName(hwnd))) return false;

        uint pid = ProcessId(hwnd);
        if (pid == Environment.ProcessId) return false;

        string process = ProcessName(pid);
        return !cfg.IgnoreProcesses.Any(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Windows on another virtual desktop (and some hidden UWP frames) are "cloaked".</summary>
    public static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttributeInt(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>All the windows you'd see in Alt+Tab, in z-order (top first).</summary>
    public static List<IntPtr> AppWindows(HorizonConfig cfg)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.GetWindow(h, Native.GW_OWNER) == IntPtr.Zero && IsManageable(h, cfg) && Title(h).Length > 0)
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
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

    public static Rectangle GetOuterBounds(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return rect.ToRectangle();
    }

    /// <summary>Moves/resizes a window so its *visible* edges land on <paramref name="target"/>.</summary>
    public static void SetVisibleBounds(IntPtr hwnd, Rectangle target)
    {
        if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var outer = GetOuterBounds(hwnd);
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

    /// <summary>
    /// Parks a window just past the right edge of all monitors (keeping its size) so a live
    /// preview can stand in for it during an animation. <see cref="RescueOffscreen"/> undoes this after a crash.
    /// </summary>
    public static void MoveOffscreen(IntPtr hwnd)
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        var outer = GetOuterBounds(hwnd);
        Native.SetWindowPos(hwnd, IntPtr.Zero, virtualScreen.Right + 400, outer.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Brings back any app window stranded completely outside every monitor.</summary>
    public static int RescueOffscreen(HorizonConfig cfg)
    {
        int rescued = 0;
        foreach (var h in AppWindows(cfg))
        {
            if (Native.IsIconic(h))
            {
                RescueMinimised(h);
                continue;
            }

            var b = GetVisibleBounds(h);
            if (Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(b))) continue;

            var wa = Screen.PrimaryScreen!.WorkingArea;
            int w = Math.Min(b.Width, wa.Width - 80), hgt = Math.Min(b.Height, wa.Height - 80);
            SetVisibleBounds(h, new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - hgt) / 2, w, hgt));
            rescued++;
        }

        return rescued;
    }

    /// <summary>A minimised window whose restore position is off every monitor gets a centred one.</summary>
    private static void RescueMinimised(IntPtr hwnd)
    {
        var pl = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(hwnd, ref pl)) return;

        var normal = pl.rcNormalPosition.ToRectangle();
        if (Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(normal))) return;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        int w = Math.Min(normal.Width, wa.Width - 80), h = Math.Min(normal.Height, wa.Height - 80);
        pl.rcNormalPosition = Native.RECT.From(new Rectangle((wa.Width - w) / 2, (wa.Height - h) / 2, w, h));
        pl.showCmd = Native.SW_SHOWMINNOACTIVE;
        Native.SetWindowPlacement(hwnd, ref pl);
    }

    /// <summary>Brings a window to the front even when Windows' focus-stealing rules would refuse.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return;

        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }
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

    private static readonly Dictionary<uint, (string Name, string? Path)> ProcessCache = new();

    private static (string Name, string? Path) ProcessInfo(uint pid)
    {
        // Called from both the UI thread and the mouse-hook thread.
        lock (ProcessCache)
        {
            if (ProcessCache.TryGetValue(pid, out var cached)) return cached;
        }

        string name = "";
        string? path = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName; }
            catch { /* elevated or protected process */ }
        }
        catch
        {
            // Process already gone.
        }

        lock (ProcessCache)
        {
            if (ProcessCache.Count > 500) ProcessCache.Clear();
            return ProcessCache[pid] = (name, path);
        }
    }

    public static string ProcessName(uint pid) => ProcessInfo(pid).Name;

    public static string ProcessName(IntPtr hwnd) => ProcessName(ProcessId(hwnd));

    public static string? ProcessPath(IntPtr hwnd) => ProcessInfo(ProcessId(hwnd)).Path;

    /// <summary>A short, human name for the app: "Spotify", "Google Chrome", or the window title.</summary>
    private static readonly Dictionary<string, string?> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    public static string FriendlyAppName(IntPtr hwnd)
    {
        string? path = ProcessPath(hwnd);
        if (path != null)
        {
            if (!DescriptionCache.TryGetValue(path, out var desc))
            {
                try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
                catch { desc = null; }
                DescriptionCache[path] = desc;
            }

            if (!string.IsNullOrWhiteSpace(desc)) return desc;
        }

        string name = ProcessName(hwnd);
        if (!string.IsNullOrEmpty(name)) return char.ToUpper(name[0]) + name[1..];
        return Title(hwnd);
    }

    /// <summary>The window's own icon, falling back to its exe's icon, then a generic one.</summary>
    public static Bitmap AppIcon(IntPtr hwnd, int size = 32)
    {
        string? path = ProcessPath(hwnd);
        if (path != null && size > 32)
        {
            var shell = ShellIcon(path, size);
            if (shell != null) return shell;
        }

        Icon? icon = null;
        try
        {
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)Native.ICON_BIG, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 200, out IntPtr hIcon);
            if (hIcon == IntPtr.Zero)
                Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)Native.ICON_SMALL2, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 200, out hIcon);
            if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
            if (hIcon != IntPtr.Zero) icon = (Icon)Icon.FromHandle(hIcon).Clone();
            if (icon == null && path != null) icon = Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            // Fall back below.
        }

        using var source = (icon ?? SystemIcons.Application).ToBitmap();
        return new Bitmap(source, size, size);
    }

    /// <summary>
    /// The crisp, large icon Explorer would show for a file or shortcut (works for .lnk, .exe
    /// and Store-app shortcuts). Returns null if the shell can't provide one.
    /// </summary>
    public static Bitmap? ShellIcon(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            var sz = new Native.SIZE { cx = size, cy = size };
            if (factory.GetImage(sz, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;

            return BitmapWithAlpha(hbm, size);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
        }
    }

    /// <summary>Image.FromHbitmap drops transparency; this keeps it.</summary>
    private static Bitmap BitmapWithAlpha(IntPtr hbm, int size)
    {
        using var opaque = Image.FromHbitmap(hbm);
        var rect = new Rectangle(0, 0, opaque.Width, opaque.Height);
        var data = opaque.LockBits(rect, ImageLockMode.ReadOnly, opaque.PixelFormat);
        try
        {
            if (Image.GetPixelFormatSize(opaque.PixelFormat) != 32)
                return new Bitmap(opaque, size, size);

            using var withAlpha = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0);
            var result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(withAlpha, new Rectangle(0, 0, size, size));
            }

            return result;
        }
        finally
        {
            opaque.UnlockBits(data);
        }
    }
}
