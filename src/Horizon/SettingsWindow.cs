namespace Horizon;

/// <summary>
/// KAMI UX Settings: every option in one calm, dark window. Changes apply the moment you make
/// them (and are saved) — no text files, no restarting.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private const int ContentWidth = 600;

    private readonly HorizonConfig _cfg;
    private readonly Action<HorizonConfig> _apply;
    private readonly Panel _scroll;
    private readonly FlowLayoutPanel _column;

    public SettingsWindow(HorizonConfig current, Action<HorizonConfig> apply, TrayActions actions)
    {
        _cfg = current.Clone();
        _apply = apply;

        Text = "KAMI UX Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(ContentWidth + 64, 820);
        MinimumSize = new Size(560, 480);
        BackColor = Kami.Bg;
        ForeColor = Kami.Text;
        Font = Kami.Font(10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = BrandIcon.Create();
        Kami.DarkTitleBar(this);
        Win.RegisterAppWindow(this);

        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Kami.Bg };
        Kami.DarkControl(_scroll);
        Controls.Add(_scroll);

        _column = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Kami.Bg,
            Location = new Point(32, 24),
            Padding = new Padding(0, 0, 0, 32)
        };
        _scroll.Controls.Add(_column);

        Build(actions);
    }

    private void Build(TrayActions actions)
    {
        _column.Controls.Add(new Label
        {
            Text = "KAMI UX",
            Font = Kami.Font(22f, FontStyle.Bold),
            ForeColor = Kami.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        });
        _column.Controls.Add(new Label
        {
            Text = "Changes apply straight away.",
            ForeColor = Kami.Sub,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 18)
        });

        Section("Windows");
        Choice("Side windows", "Live: the real window, smaller — you can use it and video keeps playing.\nMiniature: a scaled picture of the whole window; click it to bring it back.",
            new[] { "Live", "Miniature" }, _cfg.LiveSideWindows ? 0 : 1, i => _cfg.SideWindows = i == 0 ? "Live" : "Miniature");
        Toggle("Make room instead of overlapping", "Windows slide aside for each other; pushed to the sides, they shrink.",
            _cfg.AvoidOverlap, v => _cfg.AvoidOverlap = v);
        Toggle("Snap to grid lines", "Window edges click onto nearby lines when you let go.", _cfg.SnapToGrid, v => _cfg.SnapToGrid = v);
        Toggle("Drag with depth", "Windows shrink as you pull them toward the sides.", _cfg.FluidDrag, v => _cfg.FluidDrag = v);
        Toggle("Smooth animations", "Windows glide into place.", _cfg.AnimateMoves, v => _cfg.AnimateMoves = v);
        Slider("Focus zone width", "How much of the screen is full-size space.", 30, 80, (int)Math.Round(_cfg.FocusWidthRatio * 100), "%",
            v => _cfg.FocusWidthRatio = v / 100.0);
        Slider("Size at the far edge", "How small windows get at the very side.", 20, 90, (int)Math.Round(_cfg.PeripheryMinScale * 100), "%",
            v => _cfg.PeripheryMinScale = v / 100.0);

        Section("Look");
        Toggle("Living grid", "Grid lines light up around windows as you move them.", _cfg.LiveGrid, v => _cfg.LiveGrid = v);
        Toggle("Grid wallpaper", "Use the KAMI UX grid as your wallpaper.", _cfg.SetWallpaper, v => _cfg.SetWallpaper = v);
        Toggle("Hide desktop icons", "A clean background while KAMI UX runs.", _cfg.HideDesktopIcons, v => _cfg.HideDesktopIcons = v);
        Toggle("Dark mode", "Windows, Explorer and apps that follow the system go dark.", _cfg.DarkMode, v => _cfg.DarkMode = v);
        Toggle("Minimal title bars", "The same quiet dark title bar on every window.", _cfg.MinimalWindowChrome, v => _cfg.MinimalWindowChrome = v);

        Section("Dock");
        Toggle("Show the dock", "Your pinned apps, magnified under the pointer.", _cfg.ShowDock, v => _cfg.ShowDock = v);
        Toggle("Hide the Windows taskbar", "Completely, while KAMI UX runs.", _cfg.AutoHideTaskbar, v => _cfg.AutoHideTaskbar = v);
        Toggle("Files opens KAMI Files", "The dock's Files icon opens the minimal file browser.", _cfg.UseKamiFiles, v => _cfg.UseKamiFiles = v);
        Slider("Icon size", null, 32, 96, _cfg.DockIconSize, " px", v =>
        {
            _cfg.DockIconSize = v;
            _cfg.DockMagnifiedSize = Math.Max(_cfg.DockMagnifiedSize, v);
        });
        Slider("Magnified size", null, 32, 160, _cfg.DockMagnifiedSize, " px", v => _cfg.DockMagnifiedSize = Math.Max(v, _cfg.DockIconSize));

        Section("Video screens");
        Toggle("Keep video playing behind screens", "Turn off only if a video screen ever shows up blank.",
            _cfg.KeepVideoSourcesAwake, v => _cfg.KeepVideoSourcesAwake = v);

        Section("General");
        Toggle("Start with Windows", null, actions.IsStartupEnabled(), v => actions.SetStartup(v), save: false);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Kami.Bg,
            Margin = new Padding(0, 12, 0, 0),
            WrapContents = true,
            MaximumSize = new Size(ContentWidth, 0)
        };
        buttons.Controls.Add(Kami.Button("Bring every window back", (_, _) => actions.RestoreAll()));
        buttons.Controls.Add(Kami.Button("Open log folder", (_, _) => actions.OpenLogFolder()));
        buttons.Controls.Add(Kami.Button("Quit KAMI UX", (_, _) => actions.Quit()));
        _column.Controls.Add(buttons);
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    private void Section(string title)
    {
        _column.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(),
            Font = Kami.Font(8.5f, FontStyle.Bold),
            ForeColor = Kami.Sub,
            AutoSize = true,
            Margin = new Padding(4, 22, 0, 8)
        });
    }

    /// <summary>A rounded row: title and optional description on the left, a control on the right.</summary>
    private Panel Row(string title, string? description, Control control)
    {
        int height = description == null ? 52 : description.Contains('\n') ? 84 : 68;
        var row = new RoundedPanel
        {
            Size = new Size(ContentWidth, height),
            BackColor = Kami.Panel,
            Margin = new Padding(0, 0, 0, 6)
        };

        var titleLabel = new Label
        {
            Text = title,
            ForeColor = Kami.Text,
            Font = Kami.Font(10.5f),
            AutoSize = true,
            Location = new Point(16, description == null ? 16 : 12),
            BackColor = Color.Transparent
        };
        row.Controls.Add(titleLabel);

        if (description != null)
        {
            row.Controls.Add(new Label
            {
                Text = description,
                ForeColor = Kami.Sub,
                Font = Kami.Font(9f),
                AutoSize = false,
                Size = new Size(ContentWidth - control.Width - 56, height - 38),
                Location = new Point(16, 34),
                BackColor = Color.Transparent
            });
        }

        control.Location = new Point(ContentWidth - control.Width - 16, (height - control.Height) / 2);
        control.AccessibleName = title;
        row.Controls.Add(control);
        _column.Controls.Add(row);
        return row;
    }

    private void Toggle(string title, string? description, bool value, Action<bool> set, bool save = true)
    {
        var toggle = new ToggleSwitch { BackColor = Kami.Panel };
        toggle.SetQuietly(value);
        toggle.CheckedChanged += (_, _) =>
        {
            set(toggle.Checked);
            if (save) Commit();
        };
        Row(title, description, toggle);
    }

    private void Choice(string title, string description, string[] options, int selected, Action<int> set)
    {
        var seg = new Segmented(options) { BackColor = Kami.Panel, Size = new Size(200, 30) };
        seg.SetQuietly(selected);
        seg.SelectedChanged += (_, _) =>
        {
            set(seg.Selected);
            Commit();
        };
        Row(title, description, seg);
    }

    private void Slider(string title, string? description, int min, int max, int value, string unit, Action<int> set)
    {
        var holder = new Panel { Size = new Size(250, 30), BackColor = Kami.Panel };
        var slider = new KamiSlider { Minimum = min, Maximum = max, Location = new Point(0, 2), Size = new Size(190, 26), BackColor = Kami.Panel };
        slider.Value = value;
        var readout = new Label
        {
            Text = value + unit,
            ForeColor = Kami.Sub,
            AutoSize = false,
            Size = new Size(58, 30),
            Location = new Point(192, 0),
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Kami.Panel
        };
        slider.ValueChanged += (_, _) => readout.Text = slider.Value + unit;
        slider.Committed += (_, _) =>
        {
            set(slider.Value);
            Commit();
        };
        holder.Controls.Add(slider);
        holder.Controls.Add(readout);
        Row(title, description, holder);
    }

    private void Commit()
    {
        _cfg.Save();
        try { _apply(_cfg.Clone()); }
        catch (Exception ex) { Log.Write("Applying settings failed: " + ex); }
    }
}

/// <summary>A panel with softly rounded corners.</summary>
internal sealed class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Kami.Bg);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Kami.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 10);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
    }
}

/// <summary>What the Settings window can ask the app to do.</summary>
internal sealed class TrayActions
{
    public required Func<bool> IsStartupEnabled { get; init; }
    public required Action<bool> SetStartup { get; init; }
    public required Action RestoreAll { get; init; }
    public required Action OpenLogFolder { get; init; }
    public required Action Quit { get; init; }
}
