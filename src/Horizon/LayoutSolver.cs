namespace Horizon;

/// <summary>
/// Keeps windows from piling on top of each other. When a window lands somewhere, any window it
/// would cover slides aside (left/right first on a wide screen, up/down in the narrow side zones),
/// as little as possible. A window nudged out toward the sides shrinks with depth on the way, so a
/// crowded focus zone naturally spills into the periphery — windows "shoulder" each other.
/// </summary>
internal static class LayoutSolver
{
    private const int MinWidth = 200, MinHeight = 120;

    /// <summary>
    /// Where the other windows on <paramref name="screen"/> should go so none overlaps the window
    /// that just landed at <paramref name="anchorRect"/> (or each other, as far as possible).
    /// </summary>
    public static List<(IntPtr Hwnd, Rectangle To)> MakeRoom(IntPtr anchor, Rectangle anchorRect, Screen screen,
        Func<IntPtr, Size> fullSizeOf, HorizonConfig cfg, int bottomReserve)
    {
        var moves = new List<(IntPtr, Rectangle)>();
        var wa = screen.WorkingArea;
        wa.Height -= bottomReserve;
        int gap = Math.Max(8, cfg.Margin);
        var zones = ZoneLayout.For(screen.WorkingArea, cfg.FocusWidthRatio);

        var others = Win.AppWindows(cfg)
            .Where(h => h != anchor && !Native.IsIconic(h) && !Native.IsZoomed(h))
            .Select(h => (Hwnd: h, Rect: Win.GetVisibleBounds(h)))
            .Where(w => w.Rect.Width >= MinWidth && w.Rect.Height >= MinHeight && wa.IntersectsWith(w.Rect)
                        && Screen.FromRectangle(w.Rect).DeviceName == screen.DeviceName)
            .OrderBy(w => Distance(Centre(w.Rect), Centre(anchorRect)))
            .ToList();

        var placed = new List<Rectangle> { anchorRect };

        foreach (var (hwnd, original) in others)
        {
            var r = original;
            var full = fullSizeOf(hwnd);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                var blocker = placed.FirstOrDefault(p => Rectangle.Inflate(p, gap - 1, gap - 1).IntersectsWith(r));
                if (blocker.IsEmpty) break;

                var best = Rectangle.Empty;
                double bestCost = double.MaxValue;
                foreach (var (candidate, horizontal) in Candidates(r, blocker, full, gap, wa, cfg))
                {
                    if (!wa.Contains(candidate)) continue;
                    bool inFocus = Centre(candidate).X > zones.Focus.Left && Centre(candidate).X < zones.Focus.Right;
                    if (!cfg.LiveSideWindows && !inFocus) continue; // miniatures are placed by hand

                    double cost = Distance(Centre(candidate), Centre(original));
                    // Wide screen: prefer sliding sideways in the middle, stacking in the narrow sides.
                    cost *= horizontal ? (inFocus ? 0.8 : 1.3) : (inFocus ? 1.3 : 0.7);
                    if (placed.Any(p => Rectangle.Inflate(p, gap - 1, gap - 1).IntersectsWith(candidate))) cost += 5000;

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }

                if (best.IsEmpty) break; // nowhere better: leave it
                r = best;
            }

            placed.Add(r);
            if (r != original) moves.Add((hwnd, r));
        }

        return moves;
    }

    /// <summary>The four ways to step out of <paramref name="blocker"/>'s way, resized for depth.</summary>
    private static IEnumerable<(Rectangle Rect, bool Horizontal)> Candidates(Rectangle r, Rectangle blocker, Size full,
        int gap, Rectangle wa, HorizonConfig cfg)
    {
        int cy = r.Top + r.Height / 2;

        // Left: right edge sits just left of the blocker.
        {
            int right = blocker.Left - gap;
            var size = SizeAt(right - r.Width / 2, full, wa, cfg);
            yield return (new Rectangle(right - size.Width, ClampY(cy - size.Height / 2, size.Height, wa), size.Width, size.Height), true);
        }

        // Right: left edge sits just right of the blocker.
        {
            int left = blocker.Right + gap;
            var size = SizeAt(left + r.Width / 2, full, wa, cfg);
            yield return (new Rectangle(left, ClampY(cy - size.Height / 2, size.Height, wa), size.Width, size.Height), true);
        }

        // Above / below: same size, same x.
        yield return (new Rectangle(r.X, blocker.Top - gap - r.Height, r.Width, r.Height), false);
        yield return (new Rectangle(r.X, blocker.Bottom + gap, r.Width, r.Height), false);
    }

    private static Size SizeAt(int centreX, Size full, Rectangle wa, HorizonConfig cfg)
    {
        double s = cfg.LiveSideWindows ? Depth.Scale(centreX, wa, cfg) : 1;
        return new Size(Math.Max(MinWidth, (int)(full.Width * s)), Math.Max(MinHeight, (int)(full.Height * s)));
    }

    private static int ClampY(int y, int h, Rectangle wa) => Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));

    private static Point Centre(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
