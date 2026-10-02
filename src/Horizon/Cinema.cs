namespace Horizon;

/// <summary>
/// Video screens: crop the video out of any window (browser, VLC, a call…) into its own
/// borderless, resizable "screen". Make as many as you like and arrange them side by side.
/// The source window keeps playing underneath; the screen is a live, GPU-scaled view of it.
/// </summary>
internal sealed class CinemaManager
{
    private readonly List<CinemaTile> _tiles = new();
    private readonly Func<HorizonConfig> _cfg;
    private RegionPicker? _picker;

    public CinemaManager(Func<HorizonConfig> cfg) => _cfg = cfg;

    public int Count => _tiles.Count;

    /// <summary>Win+Alt+V: pick the video area in the active window.</summary>
    public void PickFromForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (_picker != null || !Win.IsManageable(hwnd, _cfg())) return;
        if (Native.IsIconic(hwnd)) return;

        var visible = Win.GetVisibleBounds(hwnd);
        _picker = new RegionPicker(visible);
        _picker.Picked += selection => Create(hwnd, selection);
        _picker.FormClosed += (_, _) => _picker = null;
        _picker.Show();
        Win.ForceForeground(_picker.Handle);
    }

    private void Create(IntPtr source, Rectangle selection)
    {
        if (!Native.IsWindow(source)) return;

        var outer = Win.GetOuterBounds(source);
        var crop = new Rectangle(selection.X - outer.X, selection.Y - outer.Y, selection.Width, selection.Height);

        var tile = new CinemaTile(source, crop, _cfg().KeepVideoSourcesAwake, this) { Bounds = selection };
        tile.FormClosed += (_, _) => _tiles.Remove(tile);
        _tiles.Add(tile);
        tile.Show();
    }

    /// <summary>Lays every screen out in a neat grid inside the focus zone of the main monitor.</summary>
    public void ArrangeAll()
    {
        if (_tiles.Count == 0) return;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        var zone = ZoneLayout.For(wa, _cfg().FocusWidthRatio).Focus;
        zone.Inflate(-24, -24);
        zone.Height -= 96; // keep clear of the dock

        int n = _tiles.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        const int gap = 16;
        int cellW = (zone.Width - gap * (cols - 1)) / cols;
        int cellH = (zone.Height - gap * (rows - 1)) / rows;

        for (int i = 0; i < n; i++)
        {
            var cell = new Rectangle(zone.Left + (i % cols) * (cellW + gap), zone.Top + (i / cols) * (cellH + gap), cellW, cellH);
            _tiles[i].AnimateTo(CinemaTile.Fit(_tiles[i].Aspect, cell));
        }
    }

    public void CloseAll()
    {
        foreach (var tile in _tiles.ToList()) tile.Close();
    }

    /// <summary>Closes screens whose source window has gone away.</summary>
    public void Prune()
    {
        foreach (var tile in _tiles.Where(t => !Native.IsWindow(t.Source)).ToList()) tile.Close();
    }
}

/// <summary>One video screen. Drag to move, drag an edge to resize (aspect kept), scroll to zoom,
/// double-click to fill the focus zone, right-click for options.</summary>
internal sealed class CinemaTile : ThumbnailView
{
    private const int Grip = 12;
    private readonly CinemaManager _owner;
    private readonly ContextMenuStrip _menu = new();
    private Rectangle? _beforeTheatre;
    private System.Windows.Forms.Timer? _anim;

    public double Aspect { get; }

    public CinemaTile(IntPtr source, Rectangle crop, bool keepSourceAwake, CinemaManager owner) : base(source, crop)
    {
        _owner = owner;
        Aspect = crop.Width / (double)Math.Max(1, crop.Height);
        BackColor = Color.Black;
        MinimumSize = new Size(160, 90);
        Text = "Video screen — " + Win.Title(source);

        // A window that's even 1/255 transparent doesn't count as covering what's behind it,
        // so browsers keep rendering the video underneath instead of pausing.
        if (keepSourceAwake) Opacity = 0.996;

        _menu.Items.Add("Fill focus zone", null, (_, _) => ToggleTheatre());
        _menu.Items.Add("Arrange all screens", null, (_, _) => _owner.ArrangeAll());
        _menu.Items.Add("Show source window", null, (_, _) => Win.ForceForeground(Source));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Close screen", null, (_, _) => Close());
        _menu.Items.Add("Close all screens", null, (_, _) => _owner.CloseAll());
    }

    public static Rectangle Fit(double aspect, Rectangle box)
    {
        int w = box.Width, h = (int)(box.Width / aspect);
        if (h > box.Height)
        {
            h = box.Height;
            w = (int)(box.Height * aspect);
        }

        return new Rectangle(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);
    }

    public void ToggleTheatre()
    {
        if (_beforeTheatre is Rectangle previous)
        {
            _beforeTheatre = null;
            AnimateTo(previous);
            return;
        }

        _beforeTheatre = Bounds;
        var wa = Screen.FromControl(this).WorkingArea;
        var zone = ZoneLayout.For(wa, 0.5).Focus;
        // Theatre uses most of the screen width, not just the focus zone, for a big picture.
        var box = Rectangle.FromLTRB(wa.Left + wa.Width / 8, wa.Top + 24, wa.Right - wa.Width / 8, wa.Bottom - 120);
        AnimateTo(Fit(Aspect, box.Width > zone.Width ? box : zone));
    }

    public void AnimateTo(Rectangle to)
    {
        _anim?.Stop();
        _anim?.Dispose();

        var from = Bounds;
        var started = Environment.TickCount64;
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) =>
        {
            double t = Math.Min(1, (Environment.TickCount64 - started) / 240.0);
            Bounds = Glide.Lerp(from, to, Depth.EaseOutCubic(t));
            if (t >= 1) _anim?.Stop();
        };
        _anim.Start();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
        int w = Math.Max(MinimumSize.Width, (int)(Width * factor));
        int h = (int)(w / Aspect);
        var c = new Point(Left + Width / 2, Top + Height / 2);
        Bounds = new Rectangle(c.X - w / 2, c.Y - h / 2, w, h);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCHITTEST:
                m.Result = (IntPtr)HitTest(PointToClient(new Point(
                    unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)))));
                return;

            case Native.WM_SIZING:
                KeepAspect(m.WParam.ToInt32(), m.LParam);
                m.Result = (IntPtr)1;
                return;

            case Native.WM_NCLBUTTONDBLCLK:
                ToggleTheatre();
                return;

            case Native.WM_NCRBUTTONUP:
                _menu.Show(Cursor.Position);
                return;
        }

        base.WndProc(ref m);
    }

    private int HitTest(Point p)
    {
        bool left = p.X < Grip, right = p.X >= Width - Grip, top = p.Y < Grip, bottom = p.Y >= Height - Grip;
        if (top && left) return Native.HTTOPLEFT;
        if (top && right) return Native.HTTOPRIGHT;
        if (bottom && left) return Native.HTBOTTOMLEFT;
        if (bottom && right) return Native.HTBOTTOMRIGHT;
        if (left) return Native.HTLEFT;
        if (right) return Native.HTRIGHT;
        if (top) return Native.HTTOP;
        if (bottom) return Native.HTBOTTOM;
        return Native.HTCAPTION; // drag anywhere to move
    }

    /// <summary>Adjusts the rectangle Windows proposes while resizing so the video keeps its shape.</summary>
    private void KeepAspect(int edge, IntPtr rectPtr)
    {
        var r = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(rectPtr);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        bool widthDriven = edge is Native.WMSZ_LEFT or Native.WMSZ_RIGHT or
            Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT or Native.WMSZ_BOTTOMLEFT or Native.WMSZ_BOTTOMRIGHT;

        if (widthDriven)
        {
            int newH = (int)(w / Aspect);
            if (edge is Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT) r.Top = r.Bottom - newH;
            else r.Bottom = r.Top + newH;
        }
        else
        {
            int newW = (int)(h * Aspect);
            r.Right = r.Left + newW;
        }

        System.Runtime.InteropServices.Marshal.StructureToPtr(r, rectPtr, false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim?.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A dimmed layer over a window: drag a box around the video (or just click for the whole window).</summary>
internal sealed class RegionPicker : Form
{
    private Point? _start;
    private Rectangle _selection;

    public event Action<Rectangle>? Picked;

    public RegionPicker(Rectangle over)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Opacity = 0.5;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Bounds = over;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_start == null) Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) { Close(); return; }
        _start = e.Location;
        _selection = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is not Point s) return;
        _selection = Rectangle.FromLTRB(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Max(s.X, e.X), Math.Max(s.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_start == null) return;

        var local = _selection.Width >= 40 && _selection.Height >= 30
            ? _selection
            : new Rectangle(0, 0, Width, Height); // a click = the whole window
        var onScreen = new Rectangle(Left + local.X, Top + local.Y, local.Width, local.Height);
        Close();
        Picked?.Invoke(onScreen);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var font = new Font("Segoe UI Semibold", 16f);
        using var text = new SolidBrush(Color.White);
        var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (_selection.Width > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(70, 70, 76));
            using var pen = new Pen(Color.White, 3f);
            g.FillRectangle(fill, _selection);
            g.DrawRectangle(pen, _selection);
        }
        else
        {
            g.DrawString("Drag around the video  ·  click for the whole window  ·  Esc to cancel",
                font, text, new RectangleF(0, 0, Width, Height), centre);
        }
    }
}
