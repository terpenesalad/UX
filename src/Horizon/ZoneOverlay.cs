using System.Drawing.Drawing2D;

namespace Horizon;

/// <summary>
/// The neon zone map shown while you drag a window: a flat grid over the focus zone,
/// a grid that curves away over each periphery, and bright stash strips at the edges.
/// Click-through and never takes focus.
/// </summary>
internal sealed class ZoneOverlay : Form
{
    private static readonly Color Ground = Color.FromArgb(10, 10, 12);
    private static readonly Color Grid = Color.FromArgb(200, 200, 210);
    private static readonly Color Label = Color.FromArgb(230, 230, 236);

    private readonly HorizonConfig _cfg;
    private Point? _cursor;

    public ZoneOverlay(Screen screen, HorizonConfig cfg)
    {
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ground;
        Opacity = 0.42;
        DoubleBuffered = true;
        Bounds = screen.WorkingArea;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT |
                          Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void TrackCursor(Point screenPoint)
    {
        var local = new Point(screenPoint.X - Bounds.Left, screenPoint.Y - Bounds.Top);
        if (_cursor == local) return;
        _cursor = local;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var area = new Rectangle(0, 0, Width, Height);
        var zones = ZoneLayout.For(area, _cfg.FocusWidthRatio);
        int edge = _cfg.StashEdgePixels;

        // Highlight whichever zone the window would land in.
        Rectangle? active = null;
        if (_cursor is Point c && area.Contains(c))
        {
            if (c.X <= edge) active = new Rectangle(0, 0, edge, Height);
            else if (c.X >= Width - 1 - edge) active = new Rectangle(Width - edge, 0, edge, Height);
            else if (zones.Left.Contains(c)) active = zones.Left;
            else if (zones.Right.Contains(c)) active = zones.Right;
            else active = zones.Focus;
        }

        if (active is Rectangle a)
        {
            using var fill = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            g.FillRectangle(fill, a);
        }

        using (var thin = new Pen(Color.FromArgb(150, Grid), 1f))
        {
            DrawFlatGrid(g, thin, zones.Focus);
            DrawCurvedGrid(g, thin, zones.Left, towardsLeft: true);
            DrawCurvedGrid(g, thin, zones.Right, towardsLeft: false);
        }

        using (var bright = new Pen(Color.FromArgb(255, 225, 225, 232), 2f) { DashStyle = DashStyle.Dash })
        {
            g.DrawLine(bright, zones.Focus.Left, 0, zones.Focus.Left, Height);
            g.DrawLine(bright, zones.Focus.Right, 0, zones.Focus.Right, Height);
        }

        using (var stash = new SolidBrush(Color.FromArgb(200, 225, 225, 232)))
        {
            g.FillRectangle(stash, 0, 0, Math.Min(edge, 6), Height);
            g.FillRectangle(stash, Width - Math.Min(edge, 6), 0, Math.Min(edge, 6), Height);
        }

        using var font = new Font("Segoe UI Semibold", 15f);
        using var small = new Font("Segoe UI", 11f);
        using var brush = new SolidBrush(Label);
        var centre = new StringFormat { Alignment = StringAlignment.Center };
        int top = Height / 2 - 30;
        g.DrawString("PERIPHERY", font, brush, new RectangleF(zones.Left.X, top, zones.Left.Width, 40), centre);
        g.DrawString("drop to park", small, brush, new RectangleF(zones.Left.X, top + 34, zones.Left.Width, 30), centre);
        g.DrawString("FOCUS", font, brush, new RectangleF(zones.Focus.X, 40, zones.Focus.Width, 40), centre);
        g.DrawString("PERIPHERY", font, brush, new RectangleF(zones.Right.X, top, zones.Right.Width, 40), centre);
        g.DrawString("drop to park", small, brush, new RectangleF(zones.Right.X, top + 34, zones.Right.Width, 30), centre);
        g.DrawString("STASH → edge", small, brush, new RectangleF(0, Height - 40, zones.Left.Width, 30), centre);
        g.DrawString("edge ← STASH", small, brush, new RectangleF(zones.Right.X, Height - 40, zones.Right.Width, 30), centre);
    }

    private static void DrawFlatGrid(Graphics g, Pen pen, Rectangle r)
    {
        const int cell = 64;
        for (int x = r.Left; x <= r.Right; x += cell) g.DrawLine(pen, x, r.Top, x, r.Bottom);
        for (int y = r.Top; y <= r.Bottom; y += cell) g.DrawLine(pen, r.Left, y, r.Right, y);
    }

    /// <summary>
    /// Lines that start flat at the focus edge and bend toward the outer edge's middle,
    /// like a screen wrapping around you. Same maths as the wallpaper.
    /// </summary>
    private static void DrawCurvedGrid(Graphics g, Pen pen, Rectangle r, bool towardsLeft)
    {
        float inner = towardsLeft ? r.Right : r.Left;
        float outer = towardsLeft ? r.Left : r.Right;
        float control = inner + (outer - inner) * 0.53f;
        float cy = r.Top + r.Height / 2f;
        const float squeeze = 0.3f;

        for (int y = r.Top - r.Height / 3; y <= r.Bottom + r.Height / 3; y += 64)
        {
            float yEnd = cy + (y - cy) * squeeze;
            // Quadratic Bézier (inner,y) → (control,y) → (outer,yEnd), drawn as the equivalent cubic.
            var p0 = new PointF(inner, y);
            var p1 = new PointF(control, y);
            var p2 = new PointF(outer, yEnd);
            g.DrawBezier(pen, p0, Lerp(p0, p1, 2f / 3f), Lerp(p2, p1, 2f / 3f), p2);
        }

        for (int k = 1; k < 15; k++)
        {
            float u = k / 15f;
            float t = 1 - (1 - u) * (1 - u); // lines bunch up toward the outer edge
            float x = (1 - t) * (1 - t) * inner + 2 * (1 - t) * t * control + t * t * outer;
            float topY = r.Top - r.Height / 3f;
            float bottomY = r.Bottom + r.Height / 3f;
            float yTop = Along(topY, cy, squeeze, t);
            float yBottom = Along(bottomY, cy, squeeze, t);
            g.DrawLine(pen, x, yTop, x, yBottom);
        }
    }

    private static float Along(float y, float cy, float squeeze, float t)
    {
        float yEnd = cy + (y - cy) * squeeze;
        return (1 - t) * (1 - t) * y + 2 * (1 - t) * t * y + t * t * yEnd;
    }

    private static PointF Lerp(PointF a, PointF b, float f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
}
