using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Win32;

namespace Horizon;

/// <summary>One icon in the dock: a pinned taskbar app and/or a running app.</summary>
internal sealed class DockItem
{
    public required string Name { get; init; }
    public required Bitmap Icon { get; init; }
    public string? LaunchPath { get; init; }   // .lnk or .exe to start it
    public string? TargetPath { get; init; }   // the exe, to match running windows
    public string? GroupKey { get; init; }     // for unpinned running apps
    public bool Pinned { get; init; }

    /// <summary>For KAMI UX's own apps (Files, Settings): what clicking does, and whether it's open.</summary>
    public Action? Open { get; set; }
    public Func<bool>? IsOpen { get; set; }

    public bool ShowsRunning => Windows.Count > 0 || (IsOpen?.Invoke() ?? false);

    public List<IntPtr> Windows { get; } = new();
    public int Cycle { get; set; }

    // Animation state
    public double Size { get; set; }
    public long BounceStart { get; set; } = -1;
    public RectangleF Bounds { get; set; }
}

/// <summary>
/// A macOS-style dock: your pinned taskbar apps (in the same order), then anything else that's
/// running. Icons magnify under the pointer, running apps get a dot, launching apps bounce.
/// Clicking a parked or stashed app brings it back to the focus zone.
/// </summary>
internal sealed class Dock : Form
{
    private const int Pad = 10, Gap = 10, DotSpace = 10, LabelSpace = 46, SeparatorWidth = 18;

    private static readonly string PinnedFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private readonly ZoneManager _zones;
    private HorizonConfig _cfg;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 800 };
    private readonly ContextMenuStrip _menu = new();
    private readonly FileSystemWatcher? _pinWatcher;

    private List<DockItem> _pinned = new();
    private List<DockItem> _items = new();
    private readonly Dictionary<string, DockItem> _running = new(StringComparer.OrdinalIgnoreCase);
    private int _mouseX = -1;
    private int _hover = -1;
    private bool _pinsChanged;

    public int PillHeight => _cfg.DockIconSize + 2 * Pad + DotSpace;

    /// <summary>KAMI UX's own apps for the dock.</summary>
    public sealed class Extras
    {
        public required Action OpenFiles { get; init; }
        public required Func<bool> FilesOpen { get; init; }
        public required Action OpenSettings { get; init; }
        public required Func<bool> SettingsOpen { get; init; }
    }

    private readonly Extras _extras;
    private readonly DockItem _filesItem;
    private readonly DockItem _settingsItem;
    private int _firstGroup; // how many icons sit before the divider

    public Dock(ZoneManager zones, HorizonConfig cfg, Extras extras)
    {
        _extras = extras;
        _filesItem = new DockItem
        {
            Name = "Files", Icon = BuiltinIcon.Files(128), Pinned = true, Size = cfg.DockIconSize,
            Open = extras.OpenFiles, IsOpen = extras.FilesOpen
        };
        _settingsItem = new DockItem
        {
            Name = "KAMI UX Settings", Icon = BuiltinIcon.Settings(128), Pinned = true, Size = cfg.DockIconSize,
            Open = extras.OpenSettings, IsOpen = extras.SettingsOpen
        };

        _zones = zones;
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "KAMI UX Dock";

        _anim.Tick += (_, _) => Render();
        _refresh.Tick += (_, _) => Refresh_();

        try
        {
            if (Directory.Exists(PinnedFolder))
            {
                _pinWatcher = new FileSystemWatcher(PinnedFolder, "*.lnk") { EnableRaisingEvents = true };
                _pinWatcher.Created += (_, _) => _pinsChanged = true;
                _pinWatcher.Deleted += (_, _) => _pinsChanged = true;
                _pinWatcher.Renamed += (_, _) => _pinsChanged = true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Can't watch pinned apps: " + ex.Message);
        }

        LoadPinned();
        Refresh_();
    }

    public void UpdateConfig(HorizonConfig cfg)
    {
        _cfg = cfg;
        foreach (var item in _items) item.Size = cfg.DockIconSize;
        Relayout();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _refresh.Start();
        Render();
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    /// <summary>Reads the shortcuts Windows keeps for pinned taskbar apps, in taskbar order.</summary>
    private void LoadPinned()
    {
        var items = new List<DockItem>();
        if (!Directory.Exists(PinnedFolder))
        {
            _pinned = items;
            return;
        }

        string order = TaskbarOrderText();
        foreach (var lnk in Directory.GetFiles(PinnedFolder, "*.lnk")
                     .OrderBy(f => OrderIndex(order, Path.GetFileName(f)))
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                var icon = Win.ShellIcon(lnk, 128) ?? new Bitmap(SystemIcons.Application.ToBitmap(), 128, 128);
                items.Add(new DockItem
                {
                    Name = name,
                    Icon = icon,
                    LaunchPath = lnk,
                    TargetPath = ShortcutTarget(lnk),
                    Pinned = true,
                    Size = _cfg.DockIconSize
                });
            }
            catch (Exception ex)
            {
                Log.Write($"Skipping pinned item {lnk}: {ex.Message}");
            }
        }

        _pinned = items;
    }

    private static int OrderIndex(string order, string fileName)
    {
        int i = order.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>The taskbar's own ordering lives in a binary registry value that contains the shortcut names.</summary>
    private static string TaskbarOrderText()
    {
        try
        {
            var blob = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband",
                "Favorites", null) as byte[];
            return blob == null ? "" : Encoding.Unicode.GetString(blob);
        }
        catch
        {
            return "";
        }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : Path.GetFullPath(target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Matches open windows to dock icons; anything unpinned gets its own icon after a divider.</summary>
    private void Refresh_()
    {
        try
        {
            if (_pinsChanged)
            {
                _pinsChanged = false;
                LoadPinned();
            }

            foreach (var item in _pinned) item.Windows.Clear();
            foreach (var item in _running.Values) item.Windows.Clear();

            foreach (var hwnd in Win.AppWindows(_cfg))
            {
                if (Win.ProcessId(hwnd) == Environment.ProcessId) continue; // Files/Settings have their own icons

                string? path = Win.ProcessPath(hwnd);
                string friendly = Win.FriendlyAppName(hwnd);

                var pinned = _pinned.FirstOrDefault(p =>
                                 path != null && p.TargetPath != null &&
                                 string.Equals(p.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                             ?? _pinned.FirstOrDefault(p => string.Equals(p.Name, friendly, StringComparison.OrdinalIgnoreCase));
                if (pinned != null)
                {
                    pinned.Windows.Add(hwnd);
                    continue;
                }

                // Store apps all run inside ApplicationFrameHost, so group those by title instead.
                bool frameHost = Win.ProcessName(hwnd).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
                string key = frameHost ? "uwp:" + Win.Title(hwnd) : path ?? Win.ProcessName(hwnd);

                if (!_running.TryGetValue(key, out var item))
                {
                    item = new DockItem
                    {
                        Name = frameHost ? Win.Title(hwnd) : friendly,
                        Icon = (path != null && !frameHost ? Win.ShellIcon(path, 128) : null) ?? Win.AppIcon(hwnd, 128),
                        LaunchPath = frameHost ? null : path,
                        TargetPath = path,
                        GroupKey = key,
                        Size = _cfg.DockIconSize
                    };
                    _running[key] = item;
                }

                item.Windows.Add(hwnd);
            }

            foreach (var key in _running.Where(kv => kv.Value.Windows.Count == 0).Select(kv => kv.Key).ToList())
            {
                if (_running.Remove(key, out var gone)) gone.Icon.Dispose();
            }

            // Files first (taking over a pinned File Explorer if there is one), then your pinned
            // apps, a divider, anything else running, and Settings at the end.
            var first = new List<DockItem>();
            bool explorerPinned = false;
            foreach (var p in _pinned)
            {
                bool isExplorer = p.TargetPath != null &&
                                  Path.GetFileName(p.TargetPath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
                if (isExplorer && _cfg.UseKamiFiles)
                {
                    explorerPinned = true;
                    _filesItem.Windows.Clear();
                    _filesItem.Windows.AddRange(p.Windows);
                    first.Add(_filesItem);
                }
                else
                {
                    first.Add(p);
                }
            }

            if (_cfg.UseKamiFiles && !explorerPinned) first.Insert(0, _filesItem);
            _firstGroup = first.Count;

            var items = first.Concat(_running.Values).Append(_settingsItem).ToList();
            bool changed = items.Count != _items.Count || !items.SequenceEqual(_items);
            _items = items;
            if (changed) Relayout();

            HideOverFullscreenApps();
            Render();
        }
        catch (Exception ex)
        {
            Log.Write("Dock refresh failed: " + ex.Message);
        }
    }

    /// <summary>Gets out of the way of games and full-screen video.</summary>
    private void HideOverFullscreenApps()
    {
        var fg = Native.GetForegroundWindow();
        bool fullscreen = false;
        if (fg != IntPtr.Zero && fg != Handle && Win.ProcessId(fg) != Environment.ProcessId)
        {
            string cls = Win.ClassName(fg);
            if (cls != "Progman" && cls != "WorkerW")
            {
                var bounds = Win.GetOuterBounds(fg);
                fullscreen = Screen.AllScreens.Any(s => s.Bounds == bounds) && !Native.IsZoomed(fg);
            }
        }

        if (fullscreen && Visible) Hide();
        else if (!fullscreen && !Visible) Show();
    }

    // ── Layout & drawing ─────────────────────────────────────────────────────

    private void Relayout()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        int n = Math.Max(1, _items.Count);
        int width = n * _cfg.DockIconSize + (n - 1) * Gap + 2 * Pad + SeparatorWidth
                    + 3 * (_cfg.DockMagnifiedSize - _cfg.DockIconSize) + 200;
        width = Math.Min(width, screen.Width);
        int height = _cfg.DockMagnifiedSize + 2 * Pad + DotSpace + LabelSpace;

        Bounds = new Rectangle(screen.Left + (screen.Width - width) / 2, screen.Bottom - height - 6, width, height);
        Render();
    }

    private bool HasSeparator => _firstGroup > 0 && _items.Count > _firstGroup;

    private void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        bool animating = StepAnimation();

        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            Draw(g);
        }

        Push(bmp);
        if (animating && !_anim.Enabled) _anim.Start();
        if (!animating && _anim.Enabled) _anim.Stop();
    }

    /// <summary>Eases each icon toward its target size. Returns true while anything is still moving.</summary>
    private bool StepAnimation()
    {
        bool moving = false;
        double baseSize = _cfg.DockIconSize, mag = _cfg.DockMagnifiedSize;
        double slot = baseSize + Gap;
        float x = RowStart(useTargets: false);

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            double target = baseSize;
            if (_mouseX >= 0)
            {
                // Distance from the pointer to this icon's resting centre, in icon widths.
                double centre = x + baseSize / 2;
                double d = (_mouseX - centre) / slot;
                target = baseSize + (mag - baseSize) * Math.Exp(-d * d / 1.8);
            }

            double next = item.Size + (target - item.Size) * 0.3;
            if (Math.Abs(next - target) > 0.3) moving = true;
            item.Size = Math.Abs(next - target) <= 0.3 ? target : next;

            if (item.BounceStart >= 0)
            {
                if (Environment.TickCount64 - item.BounceStart < 1600) moving = true;
                else item.BounceStart = -1;
            }

            x += (float)slot;
            if (HasSeparator && i == _firstGroup - 1) x += SeparatorWidth;
        }

        return moving;
    }

    /// <summary>Left edge of the first icon if every icon were at rest size (used for magnification maths).</summary>
    private float RowStart(bool useTargets)
    {
        double total = _items.Count * _cfg.DockIconSize + Math.Max(0, _items.Count - 1) * Gap + (HasSeparator ? SeparatorWidth : 0);
        return (float)((Width - total) / 2);
    }

    private void Draw(Graphics g)
    {
        if (_items.Count == 0) return;

        float total = (float)(_items.Sum(i => i.Size) + (_items.Count - 1) * Gap + (HasSeparator ? SeparatorWidth : 0));
        float x = (Width - total) / 2;
        float floor = Height - Pad - DotSpace; // icons sit on this line

        // The glass shelf.
        var shelf = new RectangleF(x - Pad, Height - PillHeight, total + 2 * Pad, PillHeight - 2);
        using (var path = RoundRect(shelf, 20))
        {
            using var fill = new SolidBrush(Color.FromArgb(200, 26, 26, 30));
            using var border = new Pen(Color.FromArgb(48, 255, 255, 255), 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            float size = (float)item.Size;
            float bounce = 0;
            if (item.BounceStart >= 0)
            {
                double t = (Environment.TickCount64 - item.BounceStart) / 1000.0;
                bounce = (float)(Math.Abs(Math.Sin(t * Math.PI * 2.2)) * 20 * Math.Max(0, 1 - t / 1.6));
            }

            var rect = new RectangleF(x, floor - size - bounce, size, size);
            item.Bounds = rect;
            g.DrawImage(item.Icon, rect);

            if (item.ShowsRunning)
            {
                using var dot = new SolidBrush(Color.FromArgb(225, 225, 230));
                g.FillEllipse(dot, x + size / 2 - 2.5f, floor + 3, 5, 5);
            }

            x += size + Gap;
            if (HasSeparator && i == _firstGroup - 1)
            {
                using var sep = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
                float sx = x - Gap / 2f + SeparatorWidth / 2f;
                g.DrawLine(sep, sx, Height - PillHeight + 12, sx, Height - 14);
                x += SeparatorWidth;
            }
        }

        if (_hover >= 0 && _hover < _items.Count) DrawLabel(g, _items[_hover]);
    }

    private void DrawLabel(Graphics g, DockItem item)
    {
        using var font = new Font("Segoe UI", 10.5f);
        var text = item.Name;
        var size = g.MeasureString(text, font);
        float w = size.Width + 22, h = size.Height + 10;
        float cx = item.Bounds.Left + item.Bounds.Width / 2;
        var rect = new RectangleF(Math.Clamp(cx - w / 2, 2, Math.Max(2, Width - w - 2)), item.Bounds.Top - h - 10, w, h);

        using var path = RoundRect(rect, 8);
        using var fill = new SolidBrush(Color.FromArgb(235, 40, 40, 45));
        using var ink = new SolidBrush(Color.FromArgb(240, 240, 244));
        g.FillPath(fill, path);
        g.DrawString(text, font, ink, rect.Left + 11, rect.Top + 5);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Hands the frame (with per-pixel transparency) to Windows.</summary>
    private void Push(Bitmap bmp)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(memDc, hBitmap);
        try
        {
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT();
            var dst = new Native.POINT { X = Left, Y = Top };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool overShelf = e.Y >= Height - PillHeight - (_cfg.DockMagnifiedSize - _cfg.DockIconSize);
        _mouseX = overShelf ? e.X : -1;
        _hover = overShelf ? _items.FindIndex(i => e.X >= i.Bounds.Left - Gap / 2f && e.X < i.Bounds.Right + Gap / 2f) : -1;
        Render();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseX = -1;
        _hover = -1;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_hover < 0 || _hover >= _items.Count) return;
        var item = _items[_hover];

        if (e.Button == MouseButtons.Right) ShowMenu(item);
        else if (e.Button == MouseButtons.Middle || ModifierKeys.HasFlag(Keys.Shift)) Launch(item);
        else if (e.Button == MouseButtons.Left) Activate(item);
    }

    private void Activate(DockItem item)
    {
        if (item.Open != null)
        {
            // Our own apps. (A pinned File Explorer's windows still come back to focus if they're away.)
            var away = item.Windows.FirstOrDefault(w => _zones.IsTracked(w));
            if (away != IntPtr.Zero) _zones.BringToFocus(away);
            else item.Open();
            return;
        }

        var windows = item.Windows.Where(Native.IsWindow).ToList();
        if (windows.Count == 0)
        {
            Launch(item);
            return;
        }

        // A parked or stashed window comes back to focus first.
        var away = windows.FirstOrDefault(w => _zones.IsTracked(w));
        if (away != IntPtr.Zero)
        {
            _zones.BringToFocus(away);
            return;
        }

        // Already in front? Cycle to the app's next window.
        var fg = Native.GetForegroundWindow();
        int current = windows.IndexOf(fg);
        var next = current >= 0 ? windows[(current + 1) % windows.Count] : windows[0];
        Win.ForceForeground(next);
    }

    private void Launch(DockItem item)
    {
        if (item.LaunchPath == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.LaunchPath) { UseShellExecute = true });
            item.BounceStart = Environment.TickCount64;
            Render();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {item.Name}: {ex.Message}");
        }
    }

    private void ShowMenu(DockItem item)
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem(item.Name) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        if (item.LaunchPath != null) _menu.Items.Add("New window", null, (_, _) => Launch(item));
        if (item.Windows.Count > 0)
        {
            _menu.Items.Add("Bring to focus", null, (_, _) => _zones.BringToFocus(item.Windows[0]));
            _menu.Items.Add(item.Windows.Count > 1 ? $"Close {item.Windows.Count} windows" : "Close window", null, (_, _) =>
            {
                foreach (var w in item.Windows) Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            });
        }

        _menu.Show(Cursor.Position);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _refresh.Dispose();
            _menu.Dispose();
            _pinWatcher?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Keeps the Windows taskbar out of the way while the dock is up: auto-hide (so windows get the
/// full screen) *and* hidden outright, so it can't pop up when the mouse touches the bottom edge.
/// Everything is put back on quit.
/// </summary>
internal static class Taskbar
{
    /// <summary>Hides every taskbar (main and other monitors). Safe to call repeatedly.</summary>
    public static void KeepHidden()
    {
        foreach (var bar in AllBars())
            if (Native.IsWindowVisible(bar)) Native.ShowWindow(bar, Native.SW_HIDE);
    }

    public static void ShowAll()
    {
        foreach (var bar in AllBars()) Native.ShowWindow(bar, Native.SW_SHOWNOACTIVATE);
    }

    private static List<IntPtr> AllBars()
    {
        var bars = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            string cls = Win.ClassName(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") bars.Add(h);
            return true;
        }, IntPtr.Zero);
        return bars;
    }

    private static string BackupPath => Path.Combine(HorizonConfig.Folder, "taskbar-backup.txt");

    public static void AutoHide()
    {
        try
        {
            int state = GetState();
            if (!File.Exists(BackupPath)) File.WriteAllText(BackupPath, state.ToString());
            SetState(state | Native.ABS_AUTOHIDE);
        }
        catch (Exception ex)
        {
            Log.Write("Could not auto-hide the taskbar: " + ex.Message);
        }
    }

    public static void Restore()
    {
        ShowAll();
        try
        {
            if (!File.Exists(BackupPath)) return;
            if (int.TryParse(File.ReadAllText(BackupPath), out int state)) SetState(state);
            File.Delete(BackupPath);
        }
        catch (Exception ex)
        {
            Log.Write("Could not restore the taskbar: " + ex.Message);
        }
    }

    private static int GetState()
    {
        var data = NewData();
        return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data);
    }

    private static void SetState(int state)
    {
        var data = NewData();
        data.lParam = (IntPtr)state;
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
    }

    private static Native.APPBARDATA NewData() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = Native.FindWindow("Shell_TrayWnd", null)
    };
}
