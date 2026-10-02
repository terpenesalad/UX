using System.Drawing.Drawing2D;

namespace Horizon;

/// <summary>
/// The background app: tray icon + menu, the window-drag hook, the zone overlay,
/// global hotkeys and a housekeeping timer.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HotkeyParkLeft = 1, HotkeyParkRight = 2, HotkeyFocus = 3, HotkeyStash = 4, HotkeyPause = 5;

    private HorizonConfig _cfg;
    private readonly ZoneManager _zones;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly HotkeyWindow _hotkeys = new();

    // The hook delegate must be kept alive for as long as the hook exists.
    private readonly Native.WinEventDelegate _hookProc;
    private readonly IntPtr _hook;

    private readonly System.Windows.Forms.Timer _dragTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer _pruneTimer = new() { Interval = 1500 };
    private readonly List<ZoneOverlay> _overlays = new();

    private IntPtr _dragging;
    private Rectangle _dragStart;
    private bool _dragIsMove;

    public TrayApp()
    {
        _cfg = HorizonConfig.Load();
        _zones = new ZoneManager(_cfg);

        _hookProc = OnWinEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        _dragTimer.Tick += (_, _) => SampleDrag();
        _pruneTimer.Tick += (_, _) => _zones.Prune();
        _pruneTimer.Start();

        var failedHotkeys = RegisterHotkeys();
        _hotkeys.HotkeyPressed += OnHotkey;

        _pauseItem = new ToolStripMenuItem("Pause Horizon", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Show zones for 2 seconds", null, (_, _) => FlashZones());
        menu.Items.Add("Bring every window back", null, (_, _) => _zones.RestoreAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open settings file", null, (_, _) => OpenSettings());
        menu.Items.Add("Reload settings", null, (_, _) => ReloadSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = BrandIcon.Create(),
            Text = "Horizon — ultra-wide zones",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => FlashZones();

        string tip = "Drag a window to a side to park it, or right to the edge to stash it.";
        if (failedHotkeys.Count > 0) tip += $"\nSome shortcuts are taken by another app: {string.Join(", ", failedHotkeys)}.";
        _tray.ShowBalloonTip(5000, "Horizon is running", tip, ToolTipIcon.None);
    }

    // ── Drag tracking ────────────────────────────────────────────────────────

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW) return;

        try
        {
            if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART) OnDragStart(hwnd);
            else if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND) OnDragEnd(hwnd);
        }
        catch (Exception ex)
        {
            Log.Write("Drag handling failed: " + ex);
        }
    }

    private void OnDragStart(IntPtr hwnd)
    {
        if (_zones.Paused || !Win.IsManageable(hwnd, _cfg)) return;

        _dragging = hwnd;
        _dragStart = Win.GetVisibleBounds(hwnd);
        _dragIsMove = false;

        if (_cfg.ShowZonesWhileDragging) ShowOverlays();
        _dragTimer.Start();
    }

    private void OnDragEnd(IntPtr hwnd)
    {
        if (hwnd != _dragging) return;

        SampleDrag();
        _dragTimer.Stop();
        HideOverlays();

        // Only react to moves. A drag that changed the size was a resize from an edge.
        if (_dragIsMove && Native.GetCursorPos(out var p))
            _zones.HandleDrop(hwnd, new Point(p.X, p.Y), _dragStart);

        _dragging = IntPtr.Zero;
    }

    private void SampleDrag()
    {
        if (_dragging == IntPtr.Zero) return;

        var now = Win.GetVisibleBounds(_dragging);
        if (now.Size == _dragStart.Size && now.Location != _dragStart.Location) _dragIsMove = true;

        if (Native.GetCursorPos(out var p))
            foreach (var overlay in _overlays) overlay.TrackCursor(new Point(p.X, p.Y));
    }

    private void ShowOverlays()
    {
        HideOverlays();
        foreach (var screen in Screen.AllScreens)
        {
            var overlay = new ZoneOverlay(screen, _cfg);
            overlay.Show();
            _overlays.Add(overlay);
        }
    }

    private void HideOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Close();
            overlay.Dispose();
        }

        _overlays.Clear();
    }

    private void FlashZones()
    {
        ShowOverlays();
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (_dragging == IntPtr.Zero) HideOverlays();
        };
        t.Start();
    }

    // ── Hotkeys ──────────────────────────────────────────────────────────────

    private List<string> RegisterHotkeys()
    {
        const uint mods = Native.MOD_WIN | Native.MOD_ALT | Native.MOD_NOREPEAT;
        var failed = new List<string>();

        void Add(int id, Keys key, string name)
        {
            if (!Native.RegisterHotKey(_hotkeys.Handle, id, mods, (uint)key)) failed.Add(name);
        }

        Add(HotkeyParkLeft, Keys.Left, "Win+Alt+Left");
        Add(HotkeyParkRight, Keys.Right, "Win+Alt+Right");
        Add(HotkeyFocus, Keys.Up, "Win+Alt+Up");
        Add(HotkeyStash, Keys.Down, "Win+Alt+Down");
        Add(HotkeyPause, Keys.P, "Win+Alt+P");
        return failed;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyParkLeft: _zones.ParkForeground(Side.Left); break;
            case HotkeyParkRight: _zones.ParkForeground(Side.Right); break;
            case HotkeyFocus: _zones.FocusForeground(); break;
            case HotkeyStash: _zones.StashForeground(Side.Left); break;
            case HotkeyPause: TogglePause(); break;
        }
    }

    // ── Menu actions ─────────────────────────────────────────────────────────

    private void TogglePause()
    {
        _zones.Paused = !_zones.Paused;
        _pauseItem.Checked = _zones.Paused;
        _tray.Text = _zones.Paused ? "Horizon — paused" : "Horizon — ultra-wide zones";
    }

    private static void OpenSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{HorizonConfig.FilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("Could not open settings: " + ex.Message);
        }
    }

    private void ReloadSettings()
    {
        _cfg = HorizonConfig.Load();
        _zones.UpdateConfig(_cfg);
        _tray.ShowBalloonTip(2000, "Horizon", "Settings reloaded.", ToolTipIcon.None);
    }

    protected override void ExitThreadCore()
    {
        _dragTimer.Stop();
        _pruneTimer.Stop();
        HideOverlays();
        if (_cfg.RestoreWindowsOnExit) _zones.RestoreAll();

        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        for (int id = HotkeyParkLeft; id <= HotkeyPause; id++) Native.UnregisterHotKey(_hotkeys.Handle, id);
        _hotkeys.DestroyHandle();

        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>An invisible window that receives global hotkey messages.</summary>
internal sealed class HotkeyWindow : NativeWindow
{
    public event Action<int>? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}

/// <summary>Draws the tray icon (a horizon arc) in code, so there are no image files to ship.</summary>
internal static class BrandIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(10, 14, 48));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(143, 162, 255), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 8, 10, 16, 16, 180, 180);
            g.DrawLine(pen, 6, 19, 26, 19);
            g.DrawLine(pen, 9, 23, 23, 23);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}
