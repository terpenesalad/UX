using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Horizon;

/// <summary>
/// Draws the dark grid for each monitor at its exact resolution (flat behind the focus zone,
/// curving away over the peripheries) and sets it as that monitor's wallpaper. Your previous
/// wallpaper is backed up first and put back on exit.
/// </summary>
internal static class WallpaperManager
{
    private static readonly Color Ground = Color.FromArgb(11, 11, 14);
    private static readonly Color GroundLift = Color.FromArgb(20, 20, 25);

    private static string BackupPath => Path.Combine(HorizonConfig.Folder, "wallpaper-backup.json");

    private sealed class Backup
    {
        public Dictionary<string, string> Paths { get; set; } = new();
        public int Position { get; set; }
    }

    /// <summary>
    /// Renders each monitor's grid on a background thread (it's a big image), then sets the
    /// wallpapers back on the UI thread, where the shell's COM object lives.
    /// </summary>
    public static void ApplyAsync(HorizonConfig cfg)
    {
        var ui = SynchronizationContext.Current;
        List<(string Id, Rectangle Bounds)> monitors;

        try
        {
            var desktop = (Native.IDesktopWallpaper)new Native.DesktopWallpaperCoClass();
            uint count = desktop.GetMonitorDevicePathCount();
            if (!File.Exists(BackupPath)) SaveBackup(desktop, count);

            monitors = new List<(string, Rectangle)>();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    string id = desktop.GetMonitorDevicePathAt(i);
                    var bounds = desktop.GetMonitorRECT(id).ToRectangle();
                    if (bounds.Width > 0 && bounds.Height > 0) monitors.Add((id, bounds));
                }
                catch
                {
                    // Monitor attached but not active.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read monitors for the wallpaper: " + ex.Message);
            return;
        }

        var jobs = monitors.Select((m, i) =>
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds == m.Bounds);
            var wa = screen?.WorkingArea ?? m.Bounds;
            var work = new Rectangle(wa.X - m.Bounds.X, wa.Y - m.Bounds.Y, wa.Width, wa.Height);
            string path = Path.Combine(HorizonConfig.Folder,
                $"kami-grid-{i}-{m.Bounds.Width}x{m.Bounds.Height}-{cfg.FocusWidthRatio:0.00}.png");
            return (m.Id, m.Bounds.Size, work, path);
        }).ToList();
        double ratio = cfg.FocusWidthRatio;

        Task.Run(() =>
        {
            foreach (var job in jobs)
            {
                if (File.Exists(job.path)) continue; // already drawn for this size
                using var bmp = Render(job.Size, job.work, ratio);
                bmp.Save(job.path, ImageFormat.Png);
            }
        }).ContinueWith(task =>
        {
            if (task.Exception != null)
            {
                Log.Write("Could not draw the wallpaper: " + task.Exception.GetBaseException().Message);
                return;
            }

            void Set()
            {
                try
                {
                    var desktop = (Native.IDesktopWallpaper)new Native.DesktopWallpaperCoClass();
                    foreach (var job in jobs) desktop.SetWallpaper(job.Id, job.path);
                    desktop.SetPosition(Native.DWPOS_FILL);
                }
                catch (Exception ex)
                {
                    Log.Write("Could not set the KAMI UX wallpaper: " + ex.Message);
                }
            }

            if (ui != null) ui.Post(_ => Set(), null);
            else Set();
        });
    }

    public static void Restore()
    {
        if (!File.Exists(BackupPath)) return;

        try
        {
            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath));
            var desktop = (Native.IDesktopWallpaper)new Native.DesktopWallpaperCoClass();
            if (backup != null)
            {
                foreach (var (id, path) in backup.Paths)
                {
                    try { desktop.SetWallpaper(id, path); }
                    catch { /* monitor no longer present */ }
                }

                desktop.SetPosition(backup.Position);
            }

            File.Delete(BackupPath);
        }
        catch (Exception ex)
        {
            Log.Write("Could not restore the previous wallpaper: " + ex.Message);
        }
    }

    private static void SaveBackup(Native.IDesktopWallpaper desktop, uint count)
    {
        var backup = new Backup();
        for (uint i = 0; i < count; i++)
        {
            try
            {
                string id = desktop.GetMonitorDevicePathAt(i);
                string current = desktop.GetWallpaper(id);
                // Never back up our own image (e.g. after a crash), or we'd "restore" the grid.
                if (!current.StartsWith(HorizonConfig.Folder, StringComparison.OrdinalIgnoreCase))
                    backup.Paths[id] = current;
            }
            catch
            {
                // Inactive monitor.
            }
        }

        try { backup.Position = desktop.GetPosition(); }
        catch { backup.Position = Native.DWPOS_FILL; }

        Directory.CreateDirectory(HorizonConfig.Folder);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
    }

    /// <summary>The grid itself. Same geometry as the drag overlay and the Lively wallpaper.</summary>
    public static Bitmap Render(Size size, Rectangle work, double focusRatio)
    {
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Ground);

        // A soft lift in the middle, like light falling on the focus area.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-size.Width * 0.1f, -size.Height * 0.35f, size.Width * 1.2f, size.Height * 1.7f);
            using var lift = new PathGradientBrush(path) { CenterColor = GroundLift, SurroundColors = new[] { Ground } };
            g.FillRectangle(lift, 0, 0, size.Width, size.Height);
        }

        var zones = ZoneLayout.For(work, focusRatio);
        float cell = Math.Max(40, size.Height / 17f);

        using var line = new Pen(Color.FromArgb(42, 255, 255, 255), 1f);
        using var curve = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
        using var edge = new Pen(Color.FromArgb(84, 255, 255, 255), 1.5f);

        // Flat grid behind focus, anchored to the centre so it looks deliberate at any size.
        float cx = zones.Focus.Left + zones.Focus.Width / 2f;
        for (float x = cx; x <= zones.Focus.Right; x += cell) g.DrawLine(line, x, 0, x, size.Height);
        for (float x = cx - cell; x >= zones.Focus.Left; x -= cell) g.DrawLine(line, x, 0, x, size.Height);
        float cy = size.Height / 2f;
        for (float y = cy; y <= size.Height; y += cell) g.DrawLine(line, zones.Focus.Left, y, zones.Focus.Right, y);
        for (float y = cy - cell; y >= 0; y -= cell) g.DrawLine(line, zones.Focus.Left, y, zones.Focus.Right, y);

        DrawCurved(g, curve, zones.Left.Right, 0, size.Height, cell);
        DrawCurved(g, curve, zones.Right.Left, size.Width, size.Height, cell);

        g.DrawLine(edge, zones.Focus.Left, 0, zones.Focus.Left, size.Height);
        g.DrawLine(edge, zones.Focus.Right, 0, zones.Focus.Right, size.Height);

        return bmp;
    }

    /// <summary>Lines leave the focus edge flat and bend toward the middle of the outer edge.</summary>
    private static void DrawCurved(Graphics g, Pen pen, float inner, float outer, int height, float cell)
    {
        const float squeeze = 0.3f;
        float control = inner + (outer - inner) * 0.53f;
        float cy = height / 2f;
        float top = -height / 3f, bottom = height * 4 / 3f;

        for (float y = cy; y <= bottom; y += cell) Curve(y);
        for (float y = cy - cell; y >= top; y -= cell) Curve(y);

        void Curve(float y)
        {
            var p0 = new PointF(inner, y);
            var p1 = new PointF(control, y);
            var p2 = new PointF(outer, cy + (y - cy) * squeeze);
            g.DrawBezier(pen, p0, Lerp(p0, p1, 2f / 3f), Lerp(p2, p1, 2f / 3f), p2);
        }

        for (int k = 1; k < 15; k++)
        {
            float u = k / 15f;
            float t = 1 - (1 - u) * (1 - u); // bunch up toward the outer edge
            float x = (1 - t) * (1 - t) * inner + 2 * (1 - t) * t * control + t * t * outer;
            float yTop = Along(top, cy, squeeze, t);
            float yBottom = Along(bottom, cy, squeeze, t);
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
