namespace Horizon;

/// <summary>
/// A window parked in the periphery: a live, scaled-down view of the whole window, contents and
/// all, like looking at it from further away. Click it to bring the window back to focus; drag it
/// to move it (it grows as it comes toward the middle, shrinks toward the edge); drop it in the
/// middle and the real window takes its place; push it to the edge to stash it.
/// </summary>
internal sealed class ParkedView : ThumbnailView
{
    private readonly ZoneManager _zones;
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 15 };
    private readonly ContextMenuStrip _menu = new();

    private bool _pressed;
    private bool _dragging;
    private Point _down;
    private Size _full;
    private PointF _grab;
    private double _scale;
    private Rectangle _rect;

    public ParkedView(IntPtr source, ZoneManager zones) : base(source, VisibleCrop(source))
    {
        _zones = zones;
        Cursor = Cursors.Hand;
        Text = Win.Title(source);
        _frame.Tick += (_, _) => Frame();

        _menu.Items.Add("Bring to focus", null, (_, _) => Later(() => _zones.BringToFocus(Source)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Close window", null, (_, _) =>
            Native.PostMessage(Source, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _pressed = true;
        _down = Control.MousePosition;
        _rect = Bounds;
        _full = _zones.FullSizeOf(Source);
        _scale = Width / (double)Math.Max(1, _full.Width);
        _grab = new PointF(e.X / (float)Math.Max(1, Width), e.Y / (float)Math.Max(1, Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || _dragging) return;

        var p = Control.MousePosition;
        var drag = SystemInformation.DragSize;
        if (Math.Abs(p.X - _down.X) <= drag.Width && Math.Abs(p.Y - _down.Y) <= drag.Height) return;

        _dragging = true;
        _frame.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Right && !_dragging)
        {
            _menu.Show(Control.MousePosition);
            return;
        }

        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;

        if (_dragging) FinishDrag();
        else
        {
            Later(() => _zones.BringToFocus(Source));
        }
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _pressed = false;
        _frame.Stop();

        var cursor = Control.MousePosition;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        double scale = Depth.Scale(cursor.X, wa, _zones.Config);
        var final = _zones.StashEdgeAt(cursor) != null ? _rect : Depth.ScaledAround(_full, scale, cursor, _grab);
        Later(() => _zones.CompleteViewDrag(this, cursor, final));
    }

    /// <summary>If something steals the mouse mid-drag (Alt+Tab, a prompt), land where we are.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging) FinishDrag();
    }

    /// <summary>Alt+F4 on a view brings its window back instead of losing it (KAMI UX closes views itself).</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;
        if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_CLOSE)
        {
            Later(() => _zones.BringToFocus(Source));
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>~60 fps while dragging: ease toward the size for this distance from the middle.</summary>
    private void Frame()
    {
        if (!_dragging) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0)
        {
            FinishDrag();
            return;
        }

        var cursor = Control.MousePosition;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        double target = Depth.Scale(cursor.X, wa, _zones.Config);
        bool atEdge = _zones.StashEdgeAt(cursor) != null;
        if (atEdge) target = Math.Min(target, 0.25);

        _scale += (target - _scale) * 0.25;
        _rect = Depth.ScaledAround(_full, _scale, cursor, _grab);
        Place(_rect, atEdge ? (byte)170 : (byte)255, topmost: true);
        _zones.ReportDrag(_rect);
    }

    /// <summary>Runs after the current mouse event finishes (the view may be closed by it).</summary>
    private void Later(Action action) => BeginInvoke(new Action(() =>
    {
        try { action(); }
        catch (Exception ex) { Log.Write("Parked view action failed: " + ex); }
    }));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}
