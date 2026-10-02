using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Horizon;

/// <summary>
/// KAMI UX's own look for its windows (Settings, Files): near-black, soft white, no colour
/// accents, rounded everything — plus a few hand-drawn controls Windows Forms doesn't have.
/// </summary>
internal static class Kami
{
    public static readonly Color Bg = Color.FromArgb(18, 18, 20);
    public static readonly Color Panel = Color.FromArgb(28, 28, 31);
    public static readonly Color PanelHover = Color.FromArgb(38, 38, 42);
    public static readonly Color Line = Color.FromArgb(48, 48, 53);
    public static readonly Color Text = Color.FromArgb(236, 236, 240);
    public static readonly Color Sub = Color.FromArgb(150, 150, 158);
    public static readonly Color Selected = Color.FromArgb(58, 58, 64);

    private static readonly string Family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text")
        ? "Segoe UI Variable Text" : "Segoe UI";

    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new(Family, size, style);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    /// <summary>Dark title bar to match, on our own windows.</summary>
    public static void DarkTitleBar(Form form)
    {
        form.HandleCreated += (_, _) =>
        {
            int on = 1, caption = 0x00141212, text = 0x00F0ECEC, border = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(form.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            Native.DwmSetWindowAttribute(form.Handle, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            Native.DwmSetWindowAttribute(form.Handle, Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
            Native.DwmSetWindowAttribute(form.Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        };
    }

    /// <summary>Dark scrollbars and headers on standard controls (lists, trees, scroll panels).</summary>
    public static void DarkControl(Control control)
    {
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        else control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Text,
            Font = Font(10f),
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = PanelHover;
        b.Click += onClick;
        return b;
    }
}

/// <summary>An on/off switch.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    private float _knob; // 0 = off, 1 = on (animated)
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        _anim.Tick += (_, _) =>
        {
            float target = _checked ? 1 : 0;
            _knob += (target - _knob) * 0.35f;
            if (Math.Abs(target - _knob) < 0.02f)
            {
                _knob = target;
                _anim.Stop();
            }

            Invalidate();
        };
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            AccessibleDescription = value ? "On" : "Off";
            _anim.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the state without animating or raising events (initial load).</summary>
    public void SetQuietly(bool value)
    {
        _checked = value;
        _knob = value ? 1 : 0;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 1, Width - 2, Height - 2);

        Color off = Color.FromArgb(58, 58, 64), on = Color.FromArgb(232, 232, 236);
        var trackColor = Blend(off, on, _knob);
        using (var path = Kami.RoundRect(track, track.Height / 2))
        using (var fill = new SolidBrush(trackColor))
            g.FillPath(fill, path);

        float d = track.Height - 6;
        float x = track.Left + 3 + (track.Width - d - 6) * _knob;
        using var knob = new SolidBrush(Blend(Color.FromArgb(214, 214, 220), Color.FromArgb(22, 22, 25), _knob));
        g.FillEllipse(knob, x, track.Top + 3, d, d);

        if (Focused)
        {
            using var ring = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f);
            using var path = Kami.RoundRect(RectangleF.Inflate(track, -0.5f, -0.5f), track.Height / 2);
            g.DrawPath(ring, path);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A minimal slider. <see cref="ValueChanged"/> fires while dragging, <see cref="Committed"/> on release.</summary>
internal sealed class KamiSlider : Control
{
    private int _value;
    private bool _dragging;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;

    public event EventHandler? ValueChanged;
    public event EventHandler? Committed;

    public KamiSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Size = new Size(180, 26);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            AccessibleDescription = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private float Fraction => Maximum == Minimum ? 0 : (_value - Minimum) / (float)(Maximum - Minimum);

    private void SetFromX(int x)
    {
        float f = Math.Clamp((x - 9) / (float)Math.Max(1, Width - 18), 0, 1);
        Value = Minimum + (int)Math.Round(f * (Maximum - Minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragging = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = Math.Max(1, (Maximum - Minimum) / 20);
        if (e.KeyCode == Keys.Left) { Value -= step; Committed?.Invoke(this, EventArgs.Empty); }
        if (e.KeyCode == Keys.Right) { Value += step; Committed?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f, left = 9, right = Width - 9;
        float x = left + (right - left) * Fraction;

        using (var bg = new Pen(Color.FromArgb(58, 58, 64), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(bg, left, cy, right, cy);
        using (var fg = new Pen(Color.FromArgb(232, 232, 236), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fg, left, cy, Math.Max(left + 0.1f, x), cy);
        using (var knob = new SolidBrush(Color.FromArgb(244, 244, 247)))
            g.FillEllipse(knob, x - 8, cy - 8, 16, 16);

        if (Focused)
        {
            using var ring = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f);
            g.DrawEllipse(ring, x - 10, cy - 10, 20, 20);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>A pill with two or more choices, one selected.</summary>
internal sealed class Segmented : Control
{
    private readonly string[] _options;
    private int _selected;

    public event EventHandler? SelectedChanged;

    public Segmented(params string[] options)
    {
        _options = options;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Size = new Size(90 * options.Length, 30);
        Font = Kami.Font(9.5f);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public int Selected
    {
        get => _selected;
        set
        {
            int v = Math.Clamp(value, 0, _options.Length - 1);
            if (v == _selected) return;
            _selected = v;
            AccessibleDescription = _options[v];
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetQuietly(int index)
    {
        _selected = Math.Clamp(index, 0, _options.Length - 1);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Selected = Math.Clamp(e.X * _options.Length / Math.Max(1, Width), 0, _options.Length - 1);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Selected--;
        if (e.KeyCode == Keys.Right) Selected++;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Kami.RoundRect(all, 8))
        using (var bg = new SolidBrush(Color.FromArgb(40, 40, 44)))
            g.FillPath(bg, path);

        float w = all.Width / _options.Length;
        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new RectangleF(all.Left + i * w + 2, all.Top + 2, w - 4, all.Height - 4);
            if (i == _selected)
            {
                using var path = Kami.RoundRect(seg, 6);
                using var sel = new SolidBrush(Color.FromArgb(232, 232, 236));
                g.FillPath(sel, path);
            }

            var color = i == _selected ? Color.FromArgb(20, 20, 22) : Kami.Text;
            TextRenderer.DrawText(g, _options[i], Font, Rectangle.Round(seg), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (Focused)
        {
            using var ring = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f);
            using var path = Kami.RoundRect(all, 8);
            g.DrawPath(ring, path);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Dock icons for KAMI UX's own apps, drawn in code (no image files to ship).</summary>
internal static class BuiltinIcon
{
    private static readonly string GlyphFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Bitmap Files(int size) => Make(size, "", Color.FromArgb(232, 232, 236));    // folder

    public static Bitmap Settings(int size) => Make(size, "", Color.FromArgb(232, 232, 236)); // gear

    private static Bitmap Make(int size, string glyph, Color ink)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        // A soft, dark rounded tile, lit slightly from the top like the dock's other icons.
        var tile = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var path = Kami.RoundRect(tile, size * 0.22f))
        {
            using var fill = new LinearGradientBrush(tile, Color.FromArgb(58, 58, 64), Color.FromArgb(30, 30, 34), 90f);
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, size / 96f));
            g.DrawPath(edge, path);
        }

        using var font = new Font(GlyphFont, size * 0.36f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(ink);
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, tile, format);
        return bmp;
    }
}

/// <summary>Dark menus to match.</summary>
internal sealed class KamiMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Kami.Panel;
    public override Color MenuItemSelected => Kami.PanelHover;
    public override Color MenuItemSelectedGradientBegin => Kami.PanelHover;
    public override Color MenuItemSelectedGradientEnd => Kami.PanelHover;
    public override Color MenuItemBorder => Kami.PanelHover;
    public override Color MenuBorder => Kami.Line;
    public override Color ImageMarginGradientBegin => Kami.Panel;
    public override Color ImageMarginGradientMiddle => Kami.Panel;
    public override Color ImageMarginGradientEnd => Kami.Panel;
    public override Color SeparatorDark => Kami.Line;
    public override Color SeparatorLight => Kami.Panel;
}

internal static class KamiMenu
{
    public static ContextMenuStrip Create() => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new KamiMenuColors()) { RoundedEdges = false },
        BackColor = Kami.Panel,
        ForeColor = Kami.Text,
        ShowImageMargin = false,
        Font = Kami.Font(10f)
    };
}
