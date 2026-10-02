namespace Horizon;

/// <summary>
/// The "pushed further away" model: a window's size depends only on how far toward
/// the side of the screen it sits. Full size anywhere in the focus zone, shrinking
/// smoothly through the periphery to <see cref="HorizonConfig.PeripheryMinScale"/> at the edge.
/// </summary>
internal static class Depth
{
    /// <summary>How much of the focus zone (each side) is used to ease into the shrink.</summary>
    private const double Lead = 0.08;

    /// <summary>0 in the focus zone → 1 at the screen edge.</summary>
    public static double Amount(int x, Rectangle workArea, HorizonConfig cfg)
    {
        var zones = ZoneLayout.For(workArea, cfg.FocusWidthRatio);
        double lead = zones.Focus.Width * Lead;
        double startLeft = zones.Focus.Left + lead;
        double startRight = zones.Focus.Right - lead;

        double t;
        if (x < startLeft) t = (startLeft - x) / Math.Max(1, startLeft - workArea.Left);
        else if (x > startRight) t = (x - startRight) / Math.Max(1, workArea.Right - startRight);
        else return 0;

        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t); // smoothstep
    }

    /// <summary>The window scale at horizontal position <paramref name="x"/>.</summary>
    public static double Scale(int x, Rectangle workArea, HorizonConfig cfg) =>
        1 - (1 - cfg.PeripheryMinScale) * Amount(x, workArea, cfg);

    /// <summary>Scales a full-size window around an anchor point, keeping that point fixed.</summary>
    public static Rectangle ScaledAround(Size full, double scale, Point anchor, PointF anchorFraction)
    {
        int w = Math.Max(120, (int)Math.Round(full.Width * scale));
        int h = Math.Max(80, (int)Math.Round(full.Height * scale));
        return new Rectangle(anchor.X - (int)(anchorFraction.X * w), anchor.Y - (int)(anchorFraction.Y * h), w, h);
    }

    /// <summary>Keeps a rectangle inside the work area (it may still be larger than it).</summary>
    public static Rectangle Clamp(Rectangle r, Rectangle workArea)
    {
        int x = Math.Clamp(r.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - r.Width));
        int y = Math.Clamp(r.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
}
