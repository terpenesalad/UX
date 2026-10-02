using System.Runtime.InteropServices;

namespace Horizon;

internal enum Side { Left, Right }

/// <summary>The three zones of one monitor's work area.</summary>
internal readonly record struct ZoneLayout(Rectangle Left, Rectangle Focus, Rectangle Right)
{
    public static ZoneLayout For(Rectangle workArea, double focusRatio)
    {
        int focusWidth = (int)(workArea.Width * focusRatio);
        int sideWidth = (workArea.Width - focusWidth) / 2;
        return new ZoneLayout(
            new Rectangle(workArea.Left, workArea.Top, sideWidth, workArea.Height),
            new Rectangle(workArea.Left + sideWidth, workArea.Top, focusWidth, workArea.Height),
            new Rectangle(workArea.Left + sideWidth + focusWidth, workArea.Top,
                workArea.Width - sideWidth - focusWidth, workArea.Height));
    }
}

/// <summary>A window Horizon has shrunk into the periphery or stashed, plus how to put it back.</summary>
internal sealed class TrackedWindow
{
    public required IntPtr Hwnd { get; init; }

    /// <summary>The window's size when it's in focus (scale 1).</summary>
    public required Size FullSize { get; set; }

    /// <summary>Where it was before Horizon first touched it (used on exit).</summary>
    public required Rectangle Original { get; init; }

    public required string ScreenName { get; set; }

    public StashWidget? Widget { get; set; }
    public Side StashSide { get; set; }
    public Rectangle StashOuter { get; set; }
    public Rectangle StashVisible { get; set; }
    public long Order { get; set; }

    public bool IsStashed => Widget != null;

    /// <summary>True once the real window has actually been minimised (after the tuck-away animation).</summary>
    public bool Tucked { get; set; }
}

/// <summary>
/// Where windows go:
///  • anywhere in the focus zone → full size;
///  • in the periphery → smaller the further out it sits (see <see cref="Depth"/>), still live;
///  • dropped at the very edge → stashed into a little widget;
///  • click the widget / dock icon, or Win+Alt+↑ → glides back to full size in focus.
/// </summary>
internal sealed class ZoneManager
{
    private readonly Dictionary<IntPtr, TrackedWindow> _tracked = new();
    private HorizonConfig _cfg;
    private long _order;

    public bool Paused { get; set; }

    /// <summary>Space kept free at the bottom of the screen for the dock.</summary>
    public int BottomReserve { get; set; }

    public ZoneManager(HorizonConfig cfg) => _cfg = cfg;

    public void UpdateConfig(HorizonConfig cfg)
    {
        _cfg = cfg;
        LayoutWidgets();
    }

    public HorizonConfig Config => _cfg;

    public bool IsTracked(IntPtr hwnd) => _tracked.ContainsKey(hwnd);

    public bool IsStashed(IntPtr hwnd) => _tracked.TryGetValue(hwnd, out var t) && t.IsStashed;

    /// <summary>The size this window has when it's in focus.</summary>
    public Size FullSizeOf(IntPtr hwnd) =>
        _tracked.TryGetValue(hwnd, out var t) ? t.FullSize : Win.GetVisibleBounds(hwnd).Size;

    /// <summary>Which edge (if any) a drop at <paramref name="cursor"/> would stash to.</summary>
    public Side? StashEdgeAt(Point cursor)
    {
        var wa = Screen.FromPoint(cursor).WorkingArea;
        if (cursor.X <= wa.Left + _cfg.StashEdgePixels) return Side.Left;
        if (cursor.X >= wa.Right - 1 - _cfg.StashEdgePixels) return Side.Right;
        return null;
    }

    // ── Results of a drag ────────────────────────────────────────────────────

    /// <summary>
    /// Called when a fluid drag ends. The real window is off-screen and <paramref name="view"/>
    /// (its live preview) sits at <paramref name="viewRect"/>.
    /// </summary>
    public void CompleteDrag(IntPtr hwnd, Point cursor, Rectangle viewRect, Size fullSize, Rectangle before, ThumbnailView view)
    {
        var screen = Screen.FromPoint(cursor);
        var t = Track(hwnd, screen, fullSize, before);

        if (StashEdgeAt(cursor) is Side side)
        {
            Stash(t, side, screen, view, viewRect);
            return;
        }

        CloseWidget(t);
        var target = Depth.Clamp(viewRect, screen.WorkingArea);
        Glide.Land(hwnd, target, activate: true, view);
        ForgetIfFullSize(t, target);
    }

    /// <summary>For drags Windows handled itself (e.g. maximised windows, keyboard moves).</summary>
    public void HandleSystemDrop(IntPtr hwnd, Point cursor, Rectangle before)
    {
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;

        var screen = Screen.FromPoint(cursor);
        var t = Track(hwnd, screen, FullSizeOf(hwnd), before);

        if (StashEdgeAt(cursor) is Side side)
        {
            Stash(t, side, screen, null, Win.GetVisibleBounds(hwnd));
            return;
        }

        var now = Win.GetVisibleBounds(hwnd);
        var anchor = new Point(cursor.X, cursor.Y);
        var fraction = new PointF(
            Math.Clamp((cursor.X - now.Left) / (float)Math.Max(1, now.Width), 0, 1),
            Math.Clamp((cursor.Y - now.Top) / (float)Math.Max(1, now.Height), 0, 1));
        double scale = Depth.Scale(cursor.X, screen.WorkingArea, _cfg);
        var target = Depth.Clamp(Depth.ScaledAround(t.FullSize, scale, anchor, fraction), screen.WorkingArea);

        Glide.Move(hwnd, target, activate: true, _cfg);
        ForgetIfFullSize(t, target);
    }

    // ── Shortcuts ────────────────────────────────────────────────────────────

    public void ParkForeground(Side side)
    {
        var hwnd = Native.GetForegroundWindow();
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;

        var screen = Screen.FromHandle(hwnd);
        var wa = screen.WorkingArea;
        var zones = ZoneLayout.For(wa, _cfg.FocusWidthRatio);
        var column = side == Side.Left ? zones.Left : zones.Right;

        var now = Win.GetVisibleBounds(hwnd);
        var t = Track(hwnd, screen, FullSizeOf(hwnd), now);
        int cx = column.Left + column.Width / 2;
        double scale = Depth.Scale(cx, wa, _cfg);
        var target = Depth.ScaledAround(t.FullSize, scale, new Point(cx, now.Top + now.Height / 2), new PointF(0.5f, 0.5f));
        Glide.Move(hwnd, Depth.Clamp(target, wa), activate: false, _cfg);
    }

    public void StashForeground(Side side)
    {
        var hwnd = Native.GetForegroundWindow();
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;

        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        var screen = Screen.FromHandle(hwnd);
        var now = Win.GetVisibleBounds(hwnd);
        var t = Track(hwnd, screen, FullSizeOf(hwnd), now);
        Stash(t, side, screen, null, now);
    }

    public void FocusForeground() => BringToFocus(Native.GetForegroundWindow());

    // ── Focus / stash ────────────────────────────────────────────────────────

    /// <summary>Brings a window back to full size in the focus zone (gliding or un-minimising).</summary>
    public void BringToFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        _tracked.Remove(hwnd, out var t);
        var screen = t != null ? FindScreen(t.ScreenName) ?? Screen.FromHandle(hwnd) : Screen.FromHandle(hwnd);
        var full = t?.FullSize ?? Win.GetVisibleBounds(hwnd).Size;

        if (t is { IsStashed: true })
        {
            CloseWidget(t);
            LayoutWidgets();
            Unstash(t, FocusRect(screen, full, null));
            return;
        }

        if (Native.IsIconic(hwnd))
        {
            Win.ForceForeground(hwnd);
            return;
        }

        var now = Win.GetVisibleBounds(hwnd);
        Glide.Move(hwnd, FocusRect(screen, full, now.Top + now.Height / 2), activate: true, _cfg);
    }

    /// <summary>Full size, centred in the focus zone, kept clear of the dock.</summary>
    private Rectangle FocusRect(Screen screen, Size full, int? centreY)
    {
        var wa = screen.WorkingArea;
        var zones = ZoneLayout.For(wa, _cfg.FocusWidthRatio);
        int m = _cfg.Margin;
        int usableBottom = wa.Bottom - BottomReserve;

        int w = Math.Min(full.Width, wa.Width - 2 * m);
        int h = Math.Min(full.Height, usableBottom - wa.Top - 2 * m);
        int x = zones.Focus.Left + (zones.Focus.Width - w) / 2;
        int cy = centreY ?? wa.Top + (usableBottom - wa.Top) / 2;
        int y = Math.Clamp(cy - h / 2, wa.Top + m, Math.Max(wa.Top + m, usableBottom - m - h));
        return new Rectangle(Math.Max(wa.Left, x), y, w, h);
    }

    private void Stash(TrackedWindow t, Side side, Screen screen, ThumbnailView? view, Rectangle viewRect)
    {
        var hwnd = t.Hwnd;
        t.StashSide = side;
        t.ScreenName = screen.DeviceName;

        if (t.Widget == null)
        {
            string app = Win.FriendlyAppName(hwnd);
            string process = Win.ProcessName(hwnd);
            bool isMedia = _cfg.MediaApps.Any(m => process.Contains(m, StringComparison.OrdinalIgnoreCase));
            t.Widget = new StashWidget(app, Win.Title(hwnd), Win.AppIcon(hwnd, 48), isMedia);
            t.Widget.RestoreRequested += () => BringToFocus(hwnd);
        }

        LayoutWidgets();
        var widget = t.Widget;

        if (view == null)
        {
            MinimiseWithFocusRestore(t, screen);
            widget.Show();
            return;
        }

        // Shrink the live preview into the widget, then tuck the real window away.
        var to = widget.Bounds;
        var started = Environment.TickCount64;
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            double k = Math.Min(1, (Environment.TickCount64 - started) / 220.0);
            view.Place(Glide.Lerp(viewRect, to, Depth.EaseOutCubic(k)));
            if (k < 1) return;

            timer.Stop();
            timer.Dispose();
            if (Native.IsWindow(hwnd)) MinimiseWithFocusRestore(t, screen);
            if (!widget.IsDisposed) widget.Show();
            view.Close();
            view.Dispose();
        };
        timer.Start();
    }

    /// <summary>
    /// Minimises a window and, in the same step, sets where it will come back: full size in the
    /// focus zone. So even restoring it from the taskbar or Alt+Tab lands it somewhere sensible
    /// (never off-screen after a drag).
    /// </summary>
    private void MinimiseWithFocusRestore(TrackedWindow t, Screen screen)
    {
        var hwnd = t.Hwnd;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var outerNow = Win.GetOuterBounds(hwnd);
        var visibleNow = Win.GetVisibleBounds(hwnd);
        var visible = FocusRect(screen, t.FullSize, null);
        var outer = Rectangle.FromLTRB(
            visible.Left - (visibleNow.Left - outerNow.Left),
            visible.Top - (visibleNow.Top - outerNow.Top),
            visible.Right + (outerNow.Right - visibleNow.Right),
            visible.Bottom + (outerNow.Bottom - visibleNow.Bottom));

        var pl = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (Native.GetWindowPlacement(hwnd, ref pl))
        {
            // Placement uses "workspace" coordinates: screen coordinates minus the taskbar's
            // offset on that monitor. (Measured from the monitor, so snapped windows are fine too.)
            var (dx, dy) = WorkspaceOffset(screen);
            var normal = outer;
            normal.Offset(dx, dy);

            pl.rcNormalPosition = Native.RECT.From(normal);
            pl.showCmd = Native.SW_SHOWMINNOACTIVE;
            pl.flags = 0;
            Native.SetWindowPlacement(hwnd, ref pl);
        }
        else
        {
            Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
        }

        t.StashOuter = outer;
        t.StashVisible = visible;
        t.Tucked = true;
    }

    /// <summary>
    /// Restores a minimised window straight into <paramref name="target"/> so Windows'
    /// own restore animation flies it there.
    /// </summary>
    private static void Unstash(TrackedWindow t, Rectangle target)
    {
        var hwnd = t.Hwnd;
        var pl = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };

        if (Native.IsIconic(hwnd) && Native.GetWindowPlacement(hwnd, ref pl))
        {
            var (dx, dy) = WorkspaceOffset(Screen.FromRectangle(target));
            int insetL = t.StashVisible.Left - t.StashOuter.Left, insetT = t.StashVisible.Top - t.StashOuter.Top;
            int insetR = t.StashOuter.Right - t.StashVisible.Right, insetB = t.StashOuter.Bottom - t.StashVisible.Bottom;

            var outer = Rectangle.FromLTRB(target.Left - insetL, target.Top - insetT,
                target.Right + insetR, target.Bottom + insetB);
            outer.Offset(dx, dy);

            pl.rcNormalPosition = Native.RECT.From(outer);
            pl.showCmd = Native.SW_RESTORE;
            pl.flags = 0;
            Native.SetWindowPlacement(hwnd, ref pl);
        }
        else
        {
            Win.SetVisibleBounds(hwnd, target);
        }

        Win.ForceForeground(hwnd);
    }

    /// <summary>Screen → workspace coordinates: subtract the space a top/left taskbar takes.</summary>
    private static (int Dx, int Dy) WorkspaceOffset(Screen screen) =>
        (screen.Bounds.Left - screen.WorkingArea.Left, screen.Bounds.Top - screen.WorkingArea.Top);

    // ── Bookkeeping ──────────────────────────────────────────────────────────

    private TrackedWindow Track(IntPtr hwnd, Screen screen, Size fullSize, Rectangle original)
    {
        if (!_tracked.TryGetValue(hwnd, out var t))
        {
            t = new TrackedWindow { Hwnd = hwnd, FullSize = fullSize, Original = original, ScreenName = screen.DeviceName };
            _tracked[hwnd] = t;
        }

        t.ScreenName = screen.DeviceName;
        t.Order = ++_order;
        return t;
    }

    private void ForgetIfFullSize(TrackedWindow t, Rectangle placed)
    {
        if (t.IsStashed) return;
        if (placed.Width >= t.FullSize.Width * 0.97) _tracked.Remove(t.Hwnd);
    }

    /// <summary>Puts every shrunk or stashed window back exactly where it started.</summary>
    public void RestoreAll()
    {
        foreach (var t in _tracked.Values.ToList())
        {
            CloseWidget(t);
            if (!Native.IsWindow(t.Hwnd)) continue;
            Win.SetVisibleBounds(t.Hwnd, t.Original);
        }

        _tracked.Clear();
        Win.RescueOffscreen(_cfg);
    }

    /// <summary>Forgets windows that closed, were maximised, or were un-minimised from the taskbar.</summary>
    public void Prune()
    {
        bool changed = false;
        foreach (var t in _tracked.Values.ToList())
        {
            bool gone = !Native.IsWindow(t.Hwnd);
            bool restoredByUser = t.IsStashed && t.Tucked && !gone &&
                                  !Native.IsIconic(t.Hwnd) && Native.IsWindowVisible(t.Hwnd);
            bool maximised = !t.IsStashed && !gone && Native.IsZoomed(t.Hwnd);
            if (!gone && !restoredByUser && !maximised) continue;

            // Restored by hand somewhere unreachable? Bring it into focus instead.
            if (restoredByUser &&
                !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Win.GetVisibleBounds(t.Hwnd))))
            {
                Win.SetVisibleBounds(t.Hwnd, FocusRect(FindScreen(t.ScreenName) ?? Screen.PrimaryScreen!, t.FullSize, null));
            }

            _tracked.Remove(t.Hwnd);
            if (t.IsStashed) changed = true;
            CloseWidget(t);
        }

        if (changed) LayoutWidgets();
    }

    /// <summary>Stacks stash widgets vertically centred on each monitor's left/right edge.</summary>
    private void LayoutWidgets()
    {
        var groups = _tracked.Values.Where(t => t.Widget != null).GroupBy(t => (t.ScreenName, t.StashSide));

        foreach (var group in groups)
        {
            var screen = FindScreen(group.Key.ScreenName) ?? Screen.PrimaryScreen!;
            var wa = screen.WorkingArea;
            var widgets = group.OrderBy(t => t.Order).Select(t => t.Widget!).ToList();

            const int gap = 12;
            int total = widgets.Sum(w => w.Height) + gap * (widgets.Count - 1);
            int y = wa.Top + Math.Max(0, (wa.Height - total) / 2);
            int x = group.Key.StashSide == Side.Left ? wa.Left + 10 : wa.Right - 10 - StashWidget.WidgetWidth;

            foreach (var widget in widgets)
            {
                widget.Location = new Point(x, y);
                y += widget.Height + gap;
            }
        }
    }

    private static void CloseWidget(TrackedWindow t)
    {
        if (t.Widget == null) return;
        t.Widget.Close();
        t.Widget.Dispose();
        t.Widget = null;
    }

    private static Screen? FindScreen(string deviceName) =>
        Screen.AllScreens.FirstOrDefault(s => s.DeviceName == deviceName);
}
