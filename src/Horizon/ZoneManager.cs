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

/// <summary>A window Horizon has parked or stashed, plus where it was before.</summary>
internal sealed class TrackedWindow
{
    public required IntPtr Hwnd { get; init; }
    public required Rectangle Original { get; set; }
    public required string ScreenName { get; set; }
    public Side? ParkedSide { get; set; }
    public Side StashSide { get; set; }
    public StashWidget? Widget { get; set; }
    public long Order { get; set; }
}

/// <summary>
/// The core behaviour:
///  • drop a window in a side zone  → it is "parked": shrunk and stacked in that column, still live;
///  • drop it at the very edge      → it is "stashed": minimised and replaced by a tiny widget;
///  • drop a parked window in focus → it gets its original size back, centred in the focus zone.
/// </summary>
internal sealed class ZoneManager
{
    private readonly Dictionary<IntPtr, TrackedWindow> _tracked = new();
    private HorizonConfig _cfg;
    private long _order;

    public bool Paused { get; set; }

    public ZoneManager(HorizonConfig cfg) => _cfg = cfg;

    public void UpdateConfig(HorizonConfig cfg)
    {
        _cfg = cfg;
        RelayoutEverything();
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    /// <summary>Called when the user finishes dragging <paramref name="hwnd"/>.</summary>
    public void HandleDrop(IntPtr hwnd, Point cursor, Rectangle boundsBeforeDrag)
    {
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;

        var screen = Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;

        if (cursor.X <= wa.Left + _cfg.StashEdgePixels)
        {
            Stash(hwnd, Side.Left, screen, boundsBeforeDrag);
            return;
        }

        if (cursor.X >= wa.Right - 1 - _cfg.StashEdgePixels)
        {
            Stash(hwnd, Side.Right, screen, boundsBeforeDrag);
            return;
        }

        var zones = ZoneLayout.For(wa, _cfg.FocusWidthRatio);
        if (zones.Left.Contains(cursor)) Park(hwnd, Side.Left, screen, boundsBeforeDrag);
        else if (zones.Right.Contains(cursor)) Park(hwnd, Side.Right, screen, boundsBeforeDrag);
        else if (_tracked.ContainsKey(hwnd)) BringToFocus(hwnd);
    }

    public void ParkForeground(Side side)
    {
        var hwnd = Native.GetForegroundWindow();
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;
        Park(hwnd, side, Screen.FromHandle(hwnd), Win.GetVisibleBounds(hwnd));
    }

    public void StashForeground(Side side)
    {
        var hwnd = Native.GetForegroundWindow();
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;
        Stash(hwnd, side, Screen.FromHandle(hwnd), Win.GetVisibleBounds(hwnd));
    }

    public void FocusForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (Paused || !Win.IsManageable(hwnd, _cfg)) return;

        if (_tracked.ContainsKey(hwnd))
        {
            BringToFocus(hwnd);
            return;
        }

        // Not parked: just centre it in the focus zone of its monitor.
        var screen = Screen.FromHandle(hwnd);
        var zones = ZoneLayout.For(screen.WorkingArea, _cfg.FocusWidthRatio);
        var b = Win.GetVisibleBounds(hwnd);
        int w = Math.Min(b.Width, zones.Focus.Width - 2 * _cfg.Margin);
        int h = Math.Min(b.Height, zones.Focus.Height - 2 * _cfg.Margin);
        Win.SetVisibleBounds(hwnd, new Rectangle(
            zones.Focus.Left + (zones.Focus.Width - w) / 2,
            zones.Focus.Top + (zones.Focus.Height - h) / 2, w, h));
    }

    // ── Park / focus / stash ─────────────────────────────────────────────────

    private TrackedWindow Track(IntPtr hwnd, Screen screen, Rectangle originalBounds)
    {
        if (!_tracked.TryGetValue(hwnd, out var t))
        {
            t = new TrackedWindow { Hwnd = hwnd, Original = originalBounds, ScreenName = screen.DeviceName };
            _tracked[hwnd] = t;
        }

        t.ScreenName = screen.DeviceName;
        t.Order = ++_order;
        return t;
    }

    private void Park(IntPtr hwnd, Side side, Screen screen, Rectangle originalBounds)
    {
        var t = Track(hwnd, screen, originalBounds);
        var previousSide = t.ParkedSide;

        CloseWidget(t);
        t.ParkedSide = side;

        LayoutParked(screen, side);
        if (previousSide.HasValue && previousSide != side) LayoutParked(screen, previousSide.Value);
    }

    private void Stash(IntPtr hwnd, Side side, Screen screen, Rectangle originalBounds)
    {
        var t = Track(hwnd, screen, originalBounds);
        var previousSide = t.ParkedSide;
        t.ParkedSide = null;
        t.StashSide = side;

        if (t.Widget == null)
        {
            string app = Win.FriendlyAppName(hwnd);
            string process = Win.ProcessName(hwnd);
            bool isMedia = _cfg.MediaApps.Any(m => process.Contains(m, StringComparison.OrdinalIgnoreCase));

            t.Widget = new StashWidget(app, Win.Title(hwnd), Win.AppIcon(hwnd), isMedia);
            t.Widget.RestoreRequested += () => BringToFocus(hwnd);
        }

        Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
        LayoutWidgets();
        t.Widget.Show();

        if (previousSide.HasValue) LayoutParked(screen, previousSide.Value);
    }

    public void BringToFocus(IntPtr hwnd)
    {
        if (!_tracked.Remove(hwnd, out var t))
        {
            Win.Activate(hwnd);
            return;
        }

        bool wasStashed = t.Widget != null;
        CloseWidget(t);

        if (!Native.IsWindow(hwnd))
        {
            if (wasStashed) LayoutWidgets();
            return;
        }

        var screen = FindScreen(t.ScreenName) ?? Screen.FromHandle(hwnd);
        var wa = screen.WorkingArea;
        var zones = ZoneLayout.For(wa, _cfg.FocusWidthRatio);
        int m = _cfg.Margin;

        int w = Math.Min(t.Original.Width, zones.Focus.Width - 2 * m);
        int h = Math.Min(t.Original.Height, wa.Height - 2 * m);
        int x = zones.Focus.Left + (zones.Focus.Width - w) / 2;
        int y = Math.Clamp(t.Original.Top, wa.Top + m, Math.Max(wa.Top + m, wa.Bottom - m - h));

        Win.SetVisibleBounds(hwnd, new Rectangle(x, y, w, h));
        Win.Activate(hwnd);

        if (t.ParkedSide.HasValue) LayoutParked(screen, t.ParkedSide.Value);
        if (wasStashed) LayoutWidgets();
    }

    /// <summary>Puts every parked/stashed window back exactly where it started.</summary>
    public void RestoreAll()
    {
        foreach (var t in _tracked.Values.ToList())
        {
            CloseWidget(t);
            if (!Native.IsWindow(t.Hwnd)) continue;
            Win.SetVisibleBounds(t.Hwnd, t.Original);
        }

        _tracked.Clear();
    }

    /// <summary>Drops windows that were closed, or un-minimised by the user from the taskbar.</summary>
    public void Prune()
    {
        bool changedWidgets = false;
        var changedColumns = new HashSet<(string, Side)>();

        foreach (var t in _tracked.Values.ToList())
        {
            bool gone = !Native.IsWindow(t.Hwnd);
            bool unstashedByUser = t.Widget != null && !gone &&
                                   !Native.IsIconic(t.Hwnd) && Native.IsWindowVisible(t.Hwnd);
            if (!gone && !unstashedByUser) continue;

            _tracked.Remove(t.Hwnd);
            if (t.Widget != null) changedWidgets = true;
            if (t.ParkedSide.HasValue) changedColumns.Add((t.ScreenName, t.ParkedSide.Value));
            CloseWidget(t);
        }

        if (changedWidgets) LayoutWidgets();
        foreach (var (screenName, side) in changedColumns)
        {
            var screen = FindScreen(screenName);
            if (screen != null) LayoutParked(screen, side);
        }
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>Stacks every window parked on one side of one monitor, top to bottom.</summary>
    private void LayoutParked(Screen screen, Side side)
    {
        var wa = screen.WorkingArea;
        var zones = ZoneLayout.For(wa, _cfg.FocusWidthRatio);
        var column = side == Side.Left ? zones.Left : zones.Right;
        int m = _cfg.Margin;

        var parked = _tracked.Values
            .Where(t => t.ParkedSide == side && t.ScreenName == screen.DeviceName && Native.IsWindow(t.Hwnd))
            .OrderBy(t => t.Order)
            .ToList();
        if (parked.Count == 0) return;

        // Leave room for stash widgets on the outer edge.
        int edgeReserve = _cfg.StashEdgePixels + StashWidget.WidgetWidth / 2;
        int x = side == Side.Left ? column.Left + Math.Max(m, edgeReserve) : column.Left + m;
        int width = column.Width - m - Math.Max(m, edgeReserve);

        int available = wa.Height - 2 * m;
        int maxEach = Math.Max(_cfg.MinParkedHeight, (available - (parked.Count - 1) * m) / parked.Count);
        int y = wa.Top + m;

        foreach (var t in parked)
        {
            double scale = width / (double)Math.Max(1, t.Original.Width);
            int height = (int)(t.Original.Height * scale);
            height = Math.Clamp(height, Math.Min(_cfg.MinParkedHeight, maxEach), maxEach);
            if (y + height > wa.Bottom - m) y = Math.Max(wa.Top + m, wa.Bottom - m - height);

            Win.SetVisibleBounds(t.Hwnd, new Rectangle(x, y, width, height));
            y += height + m;
        }
    }

    /// <summary>Stacks stash widgets vertically centred on each monitor's left/right edge.</summary>
    private void LayoutWidgets()
    {
        var groups = _tracked.Values
            .Where(t => t.Widget != null)
            .GroupBy(t => (t.ScreenName, t.StashSide));

        foreach (var group in groups)
        {
            var screen = FindScreen(group.Key.ScreenName) ?? Screen.PrimaryScreen!;
            var wa = screen.WorkingArea;
            var widgets = group.OrderBy(t => t.Order).Select(t => t.Widget!).ToList();

            const int gap = 12;
            int total = widgets.Sum(w => w.Height) + gap * (widgets.Count - 1);
            int y = wa.Top + Math.Max(0, (wa.Height - total) / 2);
            int x = group.Key.StashSide == Side.Left ? wa.Left + 8 : wa.Right - 8 - StashWidget.WidgetWidth;

            foreach (var widget in widgets)
            {
                widget.Location = new Point(x, y);
                y += widget.Height + gap;
            }
        }
    }

    private void RelayoutEverything()
    {
        foreach (var screen in Screen.AllScreens)
        {
            LayoutParked(screen, Side.Left);
            LayoutParked(screen, Side.Right);
        }

        LayoutWidgets();
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
