using System.Runtime.InteropServices;

namespace Horizon;

/// <summary>
/// A borderless window that shows a live, GPU-scaled copy of another window (a DWM thumbnail),
/// optionally cropped to part of it. Used for the drag preview, glide animations and video screens.
/// </summary>
internal class ThumbnailView : Form
{
    private IntPtr _thumb;
    private Rectangle _sourceCrop;
    private byte _opacity = 255;

    public IntPtr Source { get; }

    /// <param name="source">The window to mirror.</param>
    /// <param name="sourceCrop">The part of it to show, relative to its outer window rectangle.</param>
    public ThumbnailView(IntPtr source, Rectangle sourceCrop)
    {
        Source = source;
        _sourceCrop = sourceCrop;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(12, 12, 14);
    }

    /// <summary>The visible part of a window, in coordinates relative to its outer rectangle.</summary>
    public static Rectangle VisibleCrop(IntPtr hwnd)
    {
        var outer = Win.GetOuterBounds(hwnd);
        var vis = Win.GetVisibleBounds(hwnd);
        return new Rectangle(vis.Left - outer.Left, vis.Top - outer.Top, vis.Width, vis.Height);
    }

    public Rectangle SourceCrop
    {
        get => _sourceCrop;
        set
        {
            _sourceCrop = value;
            UpdateThumbnail();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        if (Native.DwmRegisterThumbnail(Handle, Source, out _thumb) != 0)
        {
            _thumb = IntPtr.Zero;
            Log.Write("Could not create a live preview for " + Win.Title(Source));
        }

        UpdateThumbnail();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateThumbnail();
    }

    /// <summary>Moves the view (above everything) and sets the preview's opacity in one go.</summary>
    public void Place(Rectangle bounds, byte opacity = 255, bool topmost = true)
    {
        _opacity = opacity;
        Native.SetWindowPos(Handle, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        UpdateThumbnail();
    }

    protected void UpdateThumbnail()
    {
        if (_thumb == IntPtr.Zero || !IsHandleCreated) return;

        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE |
                      Native.DWM_TNP_OPACITY | Native.DWM_TNP_VISIBLE,
            rcDestination = Native.RECT.From(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)),
            rcSource = Native.RECT.From(_sourceCrop),
            opacity = _opacity,
            fVisible = true
        };
        Native.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    protected override void Dispose(bool disposing)
    {
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Glides a real window from one place to another: a live preview does the moving,
/// the real window jumps once at the end. Smooth even for heavy apps.
/// </summary>
internal static class Glide
{
    private const int DurationMs = 260;
    private const int RevealDelayMs = 90;

    public static void Move(IntPtr hwnd, Rectangle to, bool activate, HorizonConfig cfg, Action? done = null)
    {
        var from = Win.GetVisibleBounds(hwnd);
        if (cfg.LiveSideWindows && cfg.AnimateMoves && !Native.IsIconic(hwnd) && from != to)
        {
            MoveReal(hwnd, from, to, activate, done);
            return;
        }

        if (!cfg.AnimateMoves || Native.IsIconic(hwnd) || from == to)
        {
            Win.SetVisibleBounds(hwnd, to);
            if (activate) Win.ForceForeground(hwnd);
            done?.Invoke();
            return;
        }

        var view = new ThumbnailView(hwnd, ThumbnailView.VisibleCrop(hwnd)) { Bounds = from };
        view.Show();
        view.Place(from);
        Win.MoveOffscreen(hwnd);

        var started = Environment.TickCount64;
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (Environment.TickCount64 - started) / (double)DurationMs);
            double e = Depth.EaseOutCubic(t);
            view.Place(Lerp(from, to, e));
            if (t < 1) return;

            timer.Stop();
            timer.Dispose();
            Land(hwnd, to, activate, view, done);
        };
        timer.Start();
    }

    private static readonly Dictionary<IntPtr, System.Windows.Forms.Timer> Running = new();

    /// <summary>
    /// Glides the real window itself (no stand-in), so it stays live the whole way — video keeps
    /// playing. Moves are asynchronous so a slow app can never stall KAMI UX.
    /// </summary>
    public static void MoveReal(IntPtr hwnd, Rectangle from, Rectangle to, bool activate, Action? done = null)
    {
        if (Running.Remove(hwnd, out var old))
        {
            old.Stop();
            old.Dispose();
        }

        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (activate) Win.ForceForeground(hwnd);

        var started = Environment.TickCount64;
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        Running[hwnd] = timer;
        timer.Tick += (_, _) =>
        {
            if (!Native.IsWindow(hwnd))
            {
                timer.Stop();
                Running.Remove(hwnd);
                timer.Dispose();
                return;
            }

            double t = Math.Min(1, (Environment.TickCount64 - started) / (double)DurationMs);
            Win.SetVisibleBoundsAsync(hwnd, Lerp(from, to, Depth.EaseOutCubic(t)));
            if (t < 1) return;

            timer.Stop();
            Running.Remove(hwnd);
            timer.Dispose();
            Win.SetVisibleBounds(hwnd, to); // land exactly
            done?.Invoke();
        };
        timer.Start();
    }

    /// <summary>Puts the real window under the preview, then removes the preview once it has repainted.</summary>
    public static void Land(IntPtr hwnd, Rectangle to, bool activate, ThumbnailView view, Action? done = null)
    {
        if (Native.IsWindow(hwnd))
        {
            Win.SetVisibleBounds(hwnd, to);
            if (activate) Win.ForceForeground(hwnd);
        }

        var reveal = new System.Windows.Forms.Timer { Interval = RevealDelayMs };
        reveal.Tick += (_, _) =>
        {
            reveal.Stop();
            reveal.Dispose();
            view.Close();
            view.Dispose();
            done?.Invoke();
        };
        reveal.Start();
    }

    /// <summary>Moves a live view from one place/size to another, then calls <paramref name="done"/>.</summary>
    public static void Animate(ThumbnailView view, Rectangle from, Rectangle to, HorizonConfig cfg, Action done)
    {
        if (!cfg.AnimateMoves || view.IsDisposed)
        {
            if (!view.IsDisposed) view.Place(to);
            done();
            return;
        }

        var started = Environment.TickCount64;
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (Environment.TickCount64 - started) / (double)DurationMs);
            if (!view.IsDisposed) view.Place(Lerp(from, to, Depth.EaseOutCubic(t)));
            if (t < 1 && !view.IsDisposed) return;

            timer.Stop();
            timer.Dispose();
            done();
        };
        timer.Start();
    }

    public static Rectangle Lerp(Rectangle a, Rectangle b, double t) => new(
        (int)Math.Round(a.X + (b.X - a.X) * t),
        (int)Math.Round(a.Y + (b.Y - a.Y) * t),
        (int)Math.Round(a.Width + (b.Width - a.Width) * t),
        (int)Math.Round(a.Height + (b.Height - a.Height) * t));
}
