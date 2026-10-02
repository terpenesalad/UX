namespace Horizon;

/// <summary>What part of the grid a line belongs to.</summary>
internal enum GridLineKind
{
    FocusVertical,
    FocusHorizontal,
    PeripheryVertical,
    PeripheryCurve,
    ZoneEdge
}

/// <summary>One line of the grid, as a polyline in monitor-local pixels.</summary>
internal sealed class GridLine
{
    public required GridLineKind Kind { get; init; }
    public required PointF[] Points { get; init; }

    /// <summary>For straight vertical lines: their x. For straight horizontal lines: their y. Otherwise NaN.</summary>
    public float X { get; init; } = float.NaN;
    public float Y { get; init; } = float.NaN;

    public bool IsVertical => !float.IsNaN(X);
    public bool IsHorizontal => !float.IsNaN(Y);

    /// <summary>Animated glow, 0–1 (owned by the live grid).</summary>
    public float Glow { get; set; }
    public float Target { get; set; }

    /// <summary>For curves: the curve's height at <paramref name="x"/> (linear between samples).</summary>
    public float YAt(float x)
    {
        var p = Points;
        if (p.Length == 0) return float.NaN;
        bool ascending = p[^1].X >= p[0].X;
        for (int i = 1; i < p.Length; i++)
        {
            var a = p[i - 1];
            var b = p[i];
            bool between = ascending ? x >= a.X && x <= b.X : x <= a.X && x >= b.X;
            if (!between) continue;
            float t = Math.Abs(b.X - a.X) < 0.001f ? 0 : (x - a.X) / (b.X - a.X);
            return a.Y + (b.Y - a.Y) * t;
        }

        return float.NaN;
    }
}

/// <summary>
/// The one definition of the grid. The wallpaper, the live (glowing) grid and window snapping
/// all use this, so what you see is exactly what windows line up to.
///   • a flat square grid behind the focus zone, centred on the screen;
///   • straight vertical lines in each periphery that bunch up toward the screen edge;
///   • horizontal lines in each periphery that bend toward the middle of the outer edge,
///     like a screen curving around you.
/// </summary>
internal sealed class GridGeometry
{
    private const float Squeeze = 0.3f;
    private const int CurveSamples = 48;
    private const int PeripheryVerticals = 14;

    public Size Size { get; }
    public Rectangle Work { get; }
    public ZoneLayout Zones { get; }
    public float Cell { get; }
    public List<GridLine> Lines { get; } = new();

    /// <param name="size">Monitor size in pixels.</param>
    /// <param name="work">The work area, relative to the monitor's top-left.</param>
    public GridGeometry(Size size, Rectangle work, double focusRatio)
    {
        Size = size;
        Work = work;
        Zones = ZoneLayout.For(work, focusRatio);
        Cell = Math.Max(40, size.Height / 17f);

        BuildFocus();
        BuildPeriphery(Zones.Left.Right, 0);
        BuildPeriphery(Zones.Right.Left, size.Width);

        Lines.Add(new GridLine { Kind = GridLineKind.ZoneEdge, X = Zones.Focus.Left, Points = Vertical(Zones.Focus.Left, 0, size.Height) });
        Lines.Add(new GridLine { Kind = GridLineKind.ZoneEdge, X = Zones.Focus.Right, Points = Vertical(Zones.Focus.Right, 0, size.Height) });
    }

    public static GridGeometry ForScreen(Screen screen, double focusRatio)
    {
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        return new GridGeometry(b.Size, new Rectangle(wa.X - b.X, wa.Y - b.Y, wa.Width, wa.Height), focusRatio);
    }

    private void BuildFocus()
    {
        var f = Zones.Focus;
        float cx = f.Left + f.Width / 2f, cy = Size.Height / 2f;

        foreach (float x in Steps(cx, f.Left + 1, f.Right - 1, Cell))
            Lines.Add(new GridLine { Kind = GridLineKind.FocusVertical, X = x, Points = Vertical(x, 0, Size.Height) });

        foreach (float y in Steps(cy, 0, Size.Height, Cell))
            Lines.Add(new GridLine { Kind = GridLineKind.FocusHorizontal, Y = y, Points = new[] { new PointF(f.Left, y), new PointF(f.Right, y) } });
    }

    private void BuildPeriphery(float inner, float outer)
    {
        float control = inner + (outer - inner) * 0.53f;
        float cy = Size.Height / 2f;
        float top = -Size.Height / 3f, bottom = Size.Height * 4 / 3f;

        foreach (float y in Steps(cy, top, bottom, Cell))
        {
            var pts = new PointF[CurveSamples + 1];
            var p0 = new PointF(inner, y);
            var p1 = new PointF(control, y);
            var p2 = new PointF(outer, cy + (y - cy) * Squeeze);
            for (int i = 0; i <= CurveSamples; i++) pts[i] = Quad(p0, p1, p2, i / (float)CurveSamples);
            Lines.Add(new GridLine { Kind = GridLineKind.PeripheryCurve, Points = pts });
        }

        for (int k = 1; k <= PeripheryVerticals; k++)
        {
            float u = k / (PeripheryVerticals + 1f);
            float t = 1 - (1 - u) * (1 - u); // bunch up toward the outer edge
            float x = (1 - t) * (1 - t) * inner + 2 * (1 - t) * t * control + t * t * outer;
            float yTop = Along(top, cy, t), yBottom = Along(bottom, cy, t);
            Lines.Add(new GridLine { Kind = GridLineKind.PeripheryVertical, X = x, Points = Vertical(x, yTop, yBottom) });
        }
    }

    /// <summary>
    /// Nudges a rectangle (monitor-local) so an edge that's close to a grid line sits exactly on it.
    /// </summary>
    public Rectangle Snap(Rectangle r, int distance)
    {
        int dx = BestShift(Lines.Where(l => l.IsVertical).Select(l => l.X), r.Left, r.Right, distance);
        int dy = 0;

        // Only the focus zone has straight horizontals to line tops and bottoms up with.
        int cx = r.Left + r.Width / 2;
        if (cx > Zones.Focus.Left && cx < Zones.Focus.Right)
            dy = BestShift(Lines.Where(l => l.Kind == GridLineKind.FocusHorizontal).Select(l => l.Y), r.Top, r.Bottom, distance);

        r.Offset(dx, dy);
        return r;
    }

    private static int BestShift(IEnumerable<float> lines, int a, int b, int distance)
    {
        float best = float.MaxValue;
        foreach (float line in lines)
        {
            foreach (int edge in new[] { a, b })
            {
                float d = line - edge;
                if (Math.Abs(d) <= distance && Math.Abs(d) < Math.Abs(best)) best = d;
            }
        }

        return best == float.MaxValue ? 0 : (int)Math.Round(best);
    }

    private static IEnumerable<float> Steps(float from, float min, float max, float step)
    {
        for (float v = from; v <= max; v += step) if (v >= min) yield return v;
        for (float v = from - step; v >= min; v -= step) if (v <= max) yield return v;
    }

    private static PointF[] Vertical(float x, float y0, float y1) => new[] { new PointF(x, y0), new PointF(x, y1) };

    private static float Along(float y, float cy, float t)
    {
        float yEnd = cy + (y - cy) * Squeeze;
        return (1 - t) * (1 - t) * y + 2 * (1 - t) * t * y + t * t * yEnd;
    }

    private static PointF Quad(PointF a, PointF b, PointF c, float t)
    {
        float u = 1 - t;
        return new PointF(u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
    }
}
