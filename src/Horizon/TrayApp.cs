using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Horizon;

/// <summary>
/// The background app: tray icon + menu, wiring for the fluid drag, wallpaper, dock,
/// video screens, global shortcuts and housekeeping.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HkParkLeft = 1, HkParkRight = 2, HkFocus = 3, HkStash = 4, HkPause = 5, HkVideo = 6, HkArrange = 7;
    private const string StartupName = "KAMI UX";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private HorizonConfig _cfg;
    private readonly ZoneManager _zones;
    private readonly DragController _drag;
    private readonly CinemaManager _cinema;
    private Dock? _dock;
    private SettingsWindow? _settings;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly HotkeyWindow _hotkeys = new();

    // System drags (maximised windows, keyboard moves) still go through Windows' own move loop.
    private readonly Native.WinEventDelegate _winEventProc;
    private readonly IntPtr _winEventHook;
    private IntPtr _systemDragging;
    private Rectangle _systemDragStart;
    private bool _systemDragMoved;
    private bool _systemDragWasMaximised;

    private readonly System.Windows.Forms.Timer _systemDragTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer _pruneTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _taskbarTimer = new() { Interval = 400 };

    // Windows coming to the front / appearing (Alt+Tab to a parked window, new windows to theme).
    private readonly Native.WinEventDelegate _shellEventProc;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _showHook;

    private static readonly HashSet<string> SwitcherClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd", "Windows.UI.Core.CoreWindow", "ForegroundStaging"
    };

    private string _lastForegroundClass = "";
    private readonly WindowTheme _theme;
    private LiveGrid? _grid;
    private readonly List<ZoneOverlay> _overlays = new();

    public TrayApp()
    {
        _cfg = HorizonConfig.Load();
        Win.RescueOffscreen(_cfg); // in case a previous run ended mid-drag

        _zones = new ZoneManager(_cfg);
        _cinema = new CinemaManager(() => _cfg);

        // ── Tray menu (created first: it also sets up WinForms' UI thread context) ──
        _pauseItem = new ToolStripMenuItem("Pause KAMI UX", null, (_, _) => TogglePause());
        var startup = new ToolStripMenuItem("Start with Windows", null, (s, _) => ToggleStartup((ToolStripMenuItem)s!))
        {
            Checked = IsStartupEnabled()
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => OpenSettingsWindow());
        menu.Items.Add("Files", null, (_, _) => KamiFiles.OpenOrFocus());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New video screen from active window   Win+Alt+V", null, (_, _) => _cinema.PickFromForeground());
        menu.Items.Add("Arrange video screens   Win+Alt+A", null, (_, _) => _cinema.ArrangeAll());
        menu.Items.Add("Close all video screens", null, (_, _) => _cinema.CloseAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Bring every window back", null, (_, _) => _zones.RestoreAll());
        menu.Items.Add("Show zones for 2 seconds", null, (_, _) => FlashZones());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startup);
        menu.Items.Add("Open settings file", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit KAMI UX", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = BrandIcon.Create(),
            Text = "KAMI UX",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => FlashZones();

        // ── Look: dark mode + minimal window chrome ──
        _theme = new WindowTheme(_cfg);

        // ── Fluid drag ──
        _drag = new DragController(_zones) { Enabled = _cfg.FluidDrag };
        _drag.Started += () => { if (_cfg.ShowZonesWhileDragging) ShowOverlays(); };
        _drag.Moved += p => { foreach (var o in _overlays) o.TrackCursor(p); };
        _drag.Ended += HideOverlays;

        _winEventProc = OnWinEvent;
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _systemDragTimer.Tick += (_, _) => SampleSystemDrag();

        _shellEventProc = OnShellEvent;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _shellEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _showHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _shellEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        // ── Dock, taskbar, desktop icons ──
        _taskbarTimer.Tick += (_, _) => Taskbar.KeepHidden(); // Windows sometimes brings it back
        ApplyDockAndDesktop(null);

        // ── Wallpaper (after the taskbar change has settled, so the zones line up) ──
        Delay(1200, ApplyWallpaper);

        // ── Living grid (glows under moving windows, windows snap to it) ──
        Delay(2500, () =>
        {
            _grid = new LiveGrid(_cfg);
            _zones.Snap = r => _grid?.Snap(r) ?? r;
            _zones.DragFeedback += (r, dropped) => _grid?.Feedback(r, dropped);
        });
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionEnding += OnSessionEnding; // put the taskbar/wallpaper back on sign-out too

        // ── Housekeeping + shortcuts ──
        _pruneTimer.Tick += (_, _) =>
        {
            _zones.Prune();
            _cinema.Prune();
        };
        _pruneTimer.Start();

        var failed = RegisterHotkeys();
        _hotkeys.HotkeyPressed += OnHotkey;

        string tip = "Drag a window by its title bar toward the sides — it shrinks into the distance. " +
                     "Push it to the very edge to stash it. Click a far-off window to bring it back.";
        if (failed.Count > 0) tip += $"\nShortcuts in use by another app: {string.Join(", ", failed)}.";
        _tray.ShowBalloonTip(6000, "KAMI UX is running", tip, ToolTipIcon.None);
    }

    // ── Wallpaper / display ──────────────────────────────────────────────────

    private void ApplyWallpaper()
    {
        if (_cfg.SetWallpaper) WallpaperManager.ApplyAsync(_cfg);
        else WallpaperManager.Restore();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Delay(1500, () =>
    {
        ApplyWallpaper();
        _dock?.UpdateConfig(_cfg);
        _grid?.Rebuild(_cfg);
    });

    private void OnShellEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        try
        {
            if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
            if (eventType == Native.EVENT_SYSTEM_FOREGROUND)
            {
                // Only pull a parked window in when *you* switched to it (Alt+Tab / Task View),
                // not when Windows activates it on its own after another window closes.
                string cls = Win.ClassName(hwnd);
                if (SwitcherClasses.Contains(_lastForegroundClass)) _zones.OnForegroundChanged(hwnd);
                _lastForegroundClass = cls;
            }

            _theme.OnWindowShown(hwnd);
        }
        catch (Exception ex)
        {
            Log.Write("Window event failed: " + ex.Message);
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => ExitThread();

    private static void Delay(int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            try { action(); }
            catch (Exception ex) { Log.Write("Delayed action failed: " + ex); }
        };
        t.Start();
    }

    // ── System drags (Windows' own move loop) ────────────────────────────────

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW) return;

        try
        {
            if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART)
            {
                if (_zones.Paused || !Win.IsManageable(hwnd, _cfg)) return;
                _systemDragging = hwnd;
                _systemDragStart = Win.GetVisibleBounds(hwnd);
                _systemDragWasMaximised = Native.IsZoomed(hwnd);
                _systemDragMoved = false;
                if (_cfg.ShowZonesWhileDragging) ShowOverlays();
                _systemDragTimer.Start();
            }
            else if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND && hwnd == _systemDragging)
            {
                SampleSystemDrag();
                _systemDragTimer.Stop();
                HideOverlays();
                if (_systemDragMoved && Native.GetCursorPos(out var p))
                    _zones.HandleSystemDrop(hwnd, new Point(p.X, p.Y), _systemDragStart);
                _systemDragging = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Drag handling failed: " + ex);
        }
    }

    private void SampleSystemDrag()
    {
        if (_systemDragging == IntPtr.Zero) return;

        // A move keeps the size; a resize from an edge doesn't. Only moves are ours to handle.
        // (Dragging a maximised window un-maximises it, so its size changes — that's still a move.)
        var now = Win.GetVisibleBounds(_systemDragging);
        bool sameSize = now.Size == _systemDragStart.Size || _systemDragWasMaximised;
        if (sameSize && now.Location != _systemDragStart.Location) _systemDragMoved = true;

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
        Delay(2000, HideOverlays);
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

        Add(HkParkLeft, Keys.Left, "Win+Alt+Left");
        Add(HkParkRight, Keys.Right, "Win+Alt+Right");
        Add(HkFocus, Keys.Up, "Win+Alt+Up");
        Add(HkStash, Keys.Down, "Win+Alt+Down");
        Add(HkPause, Keys.P, "Win+Alt+P");
        Add(HkVideo, Keys.V, "Win+Alt+V");
        Add(HkArrange, Keys.A, "Win+Alt+A");
        return failed;
    }

    private void OnHotkey(int id)
    {
        try
        {
            switch (id)
            {
                case HkParkLeft: _zones.ParkForeground(Side.Left); break;
                case HkParkRight: _zones.ParkForeground(Side.Right); break;
                case HkFocus: _zones.FocusForeground(); break;
                case HkStash: _zones.StashForeground(Side.Left); break;
                case HkPause: TogglePause(); break;
                case HkVideo: _cinema.PickFromForeground(); break;
                case HkArrange: _cinema.ArrangeAll(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shortcut failed: " + ex);
        }
    }

    // ── Menu actions ─────────────────────────────────────────────────────────

    private void TogglePause()
    {
        _zones.Paused = !_zones.Paused;
        _pauseItem.Checked = _zones.Paused;
        _tray.Text = _zones.Paused ? "KAMI UX — paused" : "KAMI UX";
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(StartupName) != null;
    }

    private static void SetStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue("Horizon", false); // name used by early versions
            if (on) key.SetValue(StartupName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(StartupName, false);
        }
        catch (Exception ex)
        {
            Log.Write("Could not change startup setting: " + ex.Message);
        }
    }

    private static void ToggleStartup(ToolStripMenuItem item)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (item.Checked)
            {
                key.DeleteValue(StartupName, false);
                item.Checked = false;
            }
            else
            {
                key.DeleteValue("Horizon", false); // name used by early versions
                key.SetValue(StartupName, $"\"{Environment.ProcessPath}\"");
                item.Checked = true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not change startup setting: " + ex.Message);
        }
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

    /// <summary>Applies new settings straight away (from the Settings app).</summary>
    private void ApplySettings(HorizonConfig next)
    {
        var previous = _cfg;
        _cfg = next;

        _zones.UpdateConfig(_cfg);
        _drag.Enabled = _cfg.FluidDrag;
        _theme.UpdateConfig(_cfg);
        _grid?.UpdateConfig(_cfg);
        ApplyDockAndDesktop(previous);

        bool wallpaperChanged = previous.SetWallpaper != _cfg.SetWallpaper ||
                                Math.Abs(previous.FocusWidthRatio - _cfg.FocusWidthRatio) > 0.0001;
        if (wallpaperChanged) ApplyWallpaper();
    }

    /// <summary>Dock on/off, taskbar hidden or not, desktop icons hidden or not.</summary>
    private void ApplyDockAndDesktop(HorizonConfig? previous)
    {
        if (_cfg.HideDesktopIcons) DesktopIcons.Hide();
        else DesktopIcons.Show();

        if (_cfg.ShowDock)
        {
            if (_dock == null || _dock.IsDisposed)
            {
                _dock = new Dock(_zones, _cfg, new Dock.Extras
                {
                    OpenFiles = KamiFiles.OpenOrFocus,
                    FilesOpen = () => KamiFiles.AnyOpen,
                    OpenSettings = OpenSettingsWindow,
                    SettingsOpen = () => _settings is { IsDisposed: false, Visible: true }
                });
            }
            else if (previous != null)
            {
                _dock.UpdateConfig(_cfg);
            }

            _dock.Show();
            _zones.BottomReserve = _dock.PillHeight + 16;
        }
        else
        {
            _dock?.Hide();
            _zones.BottomReserve = 0;
        }

        if (_cfg.ShowDock && _cfg.AutoHideTaskbar)
        {
            Taskbar.AutoHide();
            Taskbar.KeepHidden();
            _taskbarTimer.Start();
        }
        else
        {
            _taskbarTimer.Stop();
            Taskbar.Restore(); // shows it again (and undoes a previous run's auto-hide)
        }
    }

    private void OpenSettingsWindow()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
            _settings.Show();
            Win.ForceForeground(_settings.Handle);
            return;
        }

        _settings = new SettingsWindow(_cfg, ApplySettings, new TrayActions
        {
            IsStartupEnabled = IsStartupEnabled,
            SetStartup = SetStartup,
            RestoreAll = () => _zones.RestoreAll(),
            OpenLogFolder = () => OpenFolder(HorizonConfig.Folder),
            Quit = ExitThread
        });
        _settings.Show();
        Win.ForceForeground(_settings.Handle);
    }

    private static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Could not open folder: " + ex.Message); }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _pruneTimer.Stop();
        _systemDragTimer.Stop();
        _taskbarTimer.Stop();
        _grid?.Dispose();
        _theme.Dispose();
        HideOverlays();
        _drag.Dispose();
        _cinema.CloseAll();

        if (_cfg.RestoreWindowsOnExit) _zones.RestoreAll();
        else Win.RescueOffscreen(_cfg);
        if (_cfg.RestoreWallpaperOnExit) WallpaperManager.Restore();

        _settings?.Close();
        _dock?.Close();
        Taskbar.Restore();
        DesktopIcons.Show();

        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        if (_showHook != IntPtr.Zero) Native.UnhookWinEvent(_showHook);
        for (int id = HkParkLeft; id <= HkArrange; id++) Native.UnregisterHotKey(_hotkeys.Handle, id);
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
            using var bg = new SolidBrush(Color.FromArgb(24, 24, 28));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(236, 236, 240), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 8, 10, 16, 16, 180, 180);
            g.DrawLine(pen, 6, 19, 26, 19);
            g.DrawLine(pen, 9, 23, 23, 23);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}
