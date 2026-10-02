using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Horizon;

/// <summary>
/// The living grid: windows that sit *behind your desktop icons* (where animated wallpapers live)
/// and draw the grid. While you move a window, the lines around it light up and trail off behind
/// it; edges that land on a line glow brightest and pulse when you let go.
///
/// It runs on its own thread: being part of the desktop ties it to Explorer, so nothing the rest
/// of KAMI UX does can ever make the desktop stutter. If Windows won't give us that spot, the
/// static wallpaper is used instead and nothing breaks.
/// </summary>
internal sealed class LiveGrid : IDisposable
{
    private readonly Dictionary<string, GridGeometry> _snapGeometry = new(); // UI thread only
    private HorizonConfig _cfg;

    // Owned by the grid thread.
    private Thread? _thread;
    private Control? _dispatcher;
    private readonly List<GridSurface> _surfaces = new();

    public LiveGrid(HorizonConfig cfg)
    {
        _cfg = cfg;
        Start();
    }

    public void Rebuild(HorizonConfig cfg)
    {
        _cfg = cfg;
        _snapGeometry.Clear();
        Stop();
        Start();
    }

    private void Start()
    {
        if (!_cfg.LiveGrid) return;

        var cfg = _cfg;
        var ready = new ManualResetEventSlim(false); // not disposed: the thread may set it after we stop waiting
        _thread = new Thread(() =>
        {
            var dispatcher = new Control();
            _ = dispatcher.Handle;
            _dispatcher = dispatcher;
            BuildSurfaces(cfg);

            // Explorer restarts or wallpaper changes can destroy/hide the layer we live in.
            var watchdog = new System.Windows.Forms.Timer { Interval = 4000 };
            watchdog.Tick += (_, _) =>
            {
                if (_surfaces.Count == 0 || _surfaces.Any(s => s.IsDisposed || !s.HostAlive)) BuildSurfaces(cfg);
            };
            watchdog.Start();

            ready.Set();
            Application.Run();

            watchdog.Dispose();
            CloseSurfaces();
            dispatcher.Dispose();
        })
        { IsBackground = true, Name = "KAMI UX grid" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
    }

    private void Stop()
    {
        var d = _dispatcher;
        if (d != null && d.IsHandleCreated)
        {
            try { d.BeginInvoke(new Action(Application.ExitThread)); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        _thread?.Join(1000);
        _thread = null;
        _dispatcher = null;
    }

    /// <summary>Grid thread: (re)creates one surface per monitor inside the wallpaper layer.</summary>
    private void BuildSurfaces(HorizonConfig cfg)
    {
        CloseSurfaces();

        var host = FindWallpaperHost();
        if (host == IntPtr.Zero)
        {
            Log.Write("Live grid unavailable (no wallpaper layer found); using the static wallpaper.");
            return;
        }

        foreach (var screen in Screen.AllScreens)
        {
            try
            {
                var surface = new GridSurface(screen, GridGeometry.ForScreen(screen, cfg.FocusWidthRatio), host, cfg);
                _ = surface.Handle; // attaches to the wallpaper layer
                if (!surface.Attached)
                {
                    surface.Dispose();
                    Log.Write("Live grid couldn't attach to the wallpaper layer; using the static wallpaper.");
                    continue;
                }

                surface.Show();
                _surfaces.Add(surface);
            }
            catch (Exception ex)
            {
                Log.Write("Live grid surface failed: " + ex.Message);
            }
        }
    }

    private void CloseSurfaces()
    {
        foreach (var s in _surfaces)
        {
            if (s.IsDisposed) continue;
            s.Close();
            s.Dispose();
        }

        _surfaces.Clear();
    }

    /// <summary>Lines up a window's edges with nearby grid lines (screen coordinates). UI thread.</summary>
    public Rectangle Snap(Rectangle r)
    {
        if (!_cfg.SnapToGrid) return r;
        var screen = Screen.FromRectangle(r);
        string key = $"{screen.DeviceName}|{screen.Bounds}|{screen.WorkingArea}|{_cfg.FocusWidthRatio}";
        if (!_snapGeometry.TryGetValue(key, out var grid))
            _snapGeometry[key] = grid = GridGeometry.ForScreen(screen, _cfg.FocusWidthRatio);

        var b = screen.Bounds;
        var local = new Rectangle(r.X - b.X, r.Y - b.Y, r.Width, r.Height);
        var snapped = grid.Snap(local, _cfg.SnapDistance);
        return new Rectangle(snapped.X + b.X, snapped.Y + b.Y, r.Width, r.Height);
    }

    /// <summary>Where a window is being dragged (null when nothing is), or where it just landed.</summary>
    public void Feedback(Rectangle? rect, bool dropped)
    {
        var d = _dispatcher;
        if (d == null || !d.IsHandleCreated) return;
        try
        {
            d.BeginInvoke(new Action(() =>
            {
                foreach (var s in _surfaces)
                    if (!s.IsDisposed) s.Track(rect, dropped);
            }));
        }
        catch (InvalidOperationException)
        {
            // Grid thread is shutting down.
        }
    }

    /// <summary>
    /// Finds the layer behind the desktop icons that animated-wallpaper apps draw into
    /// (asking Explorer to create it if needed). Windows 10 and 11, including 24H2.
    /// </summary>
    private static IntPtr FindWallpaperHost()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        Native.SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)1, Native.SMTO_NORMAL, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, Native.SMTO_NORMAL, 1000, out _);

        // Windows 11 24H2+: the icons and the wallpaper layer are both children of Progman.
        if (Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            var child = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (child != IntPtr.Zero) return child;
        }

        // Older layout: the layer is the WorkerW right after the one holding the icons.
        IntPtr workerw = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                workerw = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        return workerw != IntPtr.Zero ? workerw : Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
    }

    public void Dispose() => Stop();
}

/// <summary>One monitor's piece of the living grid. Lives on the grid thread.</summary>
internal sealed class GridSurface : Form
{
    private readonly Screen _screen;
    private readonly GridGeometry _grid;
    private readonly IntPtr _host;
    private readonly HorizonConfig _cfg;
    private readonly Bitmap _base;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    private Rectangle? _drag;
    private Rectangle _last;
    private Rectangle _lastDirty;

    public bool Attached { get; private set; }

    public GridSurface(Screen screen, GridGeometry grid, IntPtr host, HorizonConfig cfg)
    {
        _screen = screen;
        _grid = grid;
        _host = host;
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(11, 11, 14);
        DoubleBuffered = true;
        Bounds = screen.Bounds;
        Text = "KAMI UX grid";

        _base = new Bitmap(grid.Size.Width, grid.Size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_base)) WallpaperManager.DrawBase(g, grid);

        _anim.Tick += (_, _) => Step();
    }

    public bool HostAlive => Native.IsWindow(_host) && Native.IsWindowVisible(_host);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Become part of the wallpaper layer, behind the desktop icons.
        int style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
        Native.SetWindowLong(Handle, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);
        if (Native.SetParent(Handle, _host) == IntPtr.Zero)
        {
            // Never show a full-screen grid on top of everything: stay detached and hidden.
            Native.SetWindowLong(Handle, Native.GWL_STYLE, style);
            Attached = false;
            return;
        }

        Attached = true;
        Native.GetWindowRect(_host, out var hostRect);
        var b = _screen.Bounds;
        Native.SetWindowPos(Handle, IntPtr.Zero, b.X - hostRect.Left, b.Y - hostRect.Top, b.Width, b.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Screen-coordinate rectangle of the window being moved (or null), or where it landed.</summary>
    public void Track(Rectangle? screenRect, bool dropped)
    {
        var b = _screen.Bounds;
        Rectangle? local = screenRect is Rectangle r && r.IntersectsWith(Rectangle.Inflate(b, (int)_grid.Cell * 3, 0))
            ? new Rectangle(r.X - b.X, r.Y - b.Y, r.Width, r.Height)
            : null;

        _drag = dropped ? null : local;
        if (local is Rectangle l) _last = l;
        SetTargets(local, dropped);
        if (!_anim.Enabled) _anim.Start();
    }

    /// <summary>How brightly each line should glow for a window at <paramref name="r"/>.</summary>
    private void SetTargets(Rectangle? r, bool dropped)
    {
        float cell = _grid.Cell;
        float snap = _cfg.SnapDistance + 1;

        foreach (var line in _grid.Lines)
        {
            float target = 0;
            bool aligned = false;

            if (r is Rectangle w)
            {
                float d; // distance from the line to the window's nearest edge (0 = touching/under)
                if (line.IsVertical)
                {
                    float edge = Math.Min(Math.Abs(line.X - w.Left), Math.Abs(line.X - w.Right));
                    aligned = edge <= snap;
                    d = line.X > w.Left && line.X < w.Right ? 0 : edge;
                }
                else
                {
                    float minX = line.Points.Min(p => p.X), maxX = line.Points.Max(p => p.X);
                    float x = Math.Clamp(w.Left + w.Width / 2f, minX, maxX);
                    float y = line.IsHorizontal ? line.Y : line.YAt(x);
                    if (float.IsNaN(y)) y = -10000;
                    float edge = Math.Min(Math.Abs(y - w.Top), Math.Abs(y - w.Bottom));
                    bool overlapsX = w.Right > minX && w.Left < maxX;
                    aligned = edge <= snap && overlapsX;
                    d = y > w.Top && y < w.Bottom ? 0 : edge;
                    if (!overlapsX) d = float.MaxValue;
                }

                target = aligned ? 1f : Math.Max(0, 1 - d / (cell * 1.8f)) * 0.55f;
                if (line.Kind == GridLineKind.ZoneEdge && line.X > w.Left && line.X < w.Right) target = Math.Max(target, 0.9f);
            }

            if (dropped)
            {
                // Lines the window landed on flash, then everything fades out.
                if (aligned) line.Glow = 1.35f;
                line.Target = 0;
            }
            else
            {
                line.Target = target;
            }
        }
    }

    private void Step()
    {
        bool moving = _drag != null;
        var dirty = Rectangle.Empty;
        var near = _drag ?? _last;

        foreach (var line in _grid.Lines)
        {
            float k = line.Target > line.Glow ? 0.35f : 0.08f; // light up fast, fade slowly (a trail)
            line.Glow += (line.Target - line.Glow) * k;
            if (line.Glow < 0.004f && line.Target == 0) line.Glow = 0;
            if (line.Glow <= 0) continue;

            moving = true;
            var seg = GlowSegment(line, near);
            if (seg.Length >= 2) dirty = Union(dirty, SegmentBounds(seg));
        }

        // Repaint only what's glowing now plus what glowed last frame (so it fades cleanly).
        var region = Union(dirty, _lastDirty);
        if (!region.IsEmpty) Invalidate(Rectangle.Inflate(region, 10, 10));
        _lastDirty = dirty;
        if (!moving) _anim.Stop();
    }

    private static Rectangle Union(Rectangle a, Rectangle b) => a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b);

    private static Rectangle SegmentBounds(PointF[] pts)
    {
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        return Rectangle.FromLTRB((int)minX, (int)minY, (int)Math.Ceiling(maxX) + 1, (int)Math.Ceiling(maxY) + 1);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var near = _drag ?? _last;
        foreach (var line in _grid.Lines)
        {
            if (line.Glow <= 0.01f) continue;
            var pts = GlowSegment(line, near);
            if (pts.Length < 2) continue;

            float glow = Math.Min(1.35f, line.Glow);
            DrawGlow(g, pts, (int)Math.Min(255, 70 * glow), 7f);
            DrawGlow(g, pts, (int)Math.Min(255, 235 * glow), 1.6f);
        }
    }

    /// <summary>The part of a line that lights up: near the window, fading out along the line.</summary>
    private PointF[] GlowSegment(GridLine line, Rectangle near)
    {
        if (near.IsEmpty) return line.Points;
        var area = RectangleF.Inflate(near, _grid.Cell * 3, _grid.Cell * 3);

        if (line.IsVertical)
        {
            float top = Math.Max(area.Top, line.Points[0].Y), bottom = Math.Min(area.Bottom, line.Points[^1].Y);
            return bottom - top < 2 ? Array.Empty<PointF>() : new[] { new PointF(line.X, top), new PointF(line.X, bottom) };
        }

        var inside = line.Points.Where(p => p.X >= area.Left && p.X <= area.Right).ToArray();
        return inside.Length >= 2 ? inside : Array.Empty<PointF>();
    }

    private static void DrawGlow(Graphics g, PointF[] pts, int alpha, float width)
    {
        var a = pts[0];
        var b = pts[^1];
        if (Math.Abs(a.X - b.X) < 1f && Math.Abs(a.Y - b.Y) < 1f) return; // GDI+ can't fade along zero length

        // Bright in the middle, fading to nothing at both ends.
        var clear = Color.FromArgb(0, 255, 255, 255);
        var lit = Color.FromArgb(alpha, 255, 255, 255);
        using var brush = new LinearGradientBrush(a, b, lit, lit)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { clear, lit, lit, clear },
                Positions = new[] { 0f, 0.3f, 0.7f, 1f }
            }
        };
        using var pen = new Pen(brush, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _base.Dispose();
        }

        base.Dispose(disposing);
    }
}
