namespace Horizon;

/// <summary>
/// The tiny control a stashed window collapses into: its icon (click to bring it back)
/// and, for music apps, a play/pause button — "just the one useful control".
/// </summary>
internal sealed class StashWidget : Form
{
    public const int WidgetWidth = 84;

    private static readonly Color Background = Color.FromArgb(20, 22, 31);
    private static readonly Color Border = Color.FromArgb(70, 84, 160);
    private static readonly Color Tile = Color.FromArgb(34, 40, 58);
    private static readonly Color TextColor = Color.FromArgb(201, 208, 219);
    private static readonly Color Accent = Color.FromArgb(232, 236, 247);

    public event Action? RestoreRequested;

    private readonly ToolTip _tip = new();

    public StashWidget(string appName, string windowTitle, Image icon, bool isMedia)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Background;
        DoubleBuffered = true;
        Size = new Size(WidgetWidth, isMedia ? 176 : 116);
        Text = appName;

        var restore = new Button
        {
            FlatStyle = FlatStyle.Flat,
            Size = new Size(56, 56),
            Location = new Point((WidgetWidth - 56) / 2, 14),
            BackColor = Tile,
            Image = icon,
            Cursor = Cursors.Hand,
            AccessibleName = $"Bring back {appName}"
        };
        restore.FlatAppearance.BorderSize = 0;
        restore.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 62, 96);
        restore.Click += (_, _) => RestoreRequested?.Invoke();
        _tip.SetToolTip(restore, $"{windowTitle}\nClick to bring it back to focus");
        Controls.Add(restore);

        var label = new Label
        {
            Text = appName,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 8f),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(4, 76),
            Size = new Size(WidgetWidth - 8, 20)
        };
        Controls.Add(label);

        if (isMedia)
        {
            var play = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Size = new Size(48, 48),
                Location = new Point((WidgetWidth - 48) / 2, 108),
                BackColor = Accent,
                ForeColor = Background,
                Font = new Font("Segoe UI Symbol", 14f),
                Text = "⏯", // ⏯
                Cursor = Cursors.Hand,
                AccessibleName = "Play or pause"
            };
            play.FlatAppearance.BorderSize = 0;
            play.Click += (_, _) => Native.PressMediaKey(Native.VK_MEDIA_PLAY_PAUSE);
            RoundOff(play, 48); // a circle
            _tip.SetToolTip(play, "Play / pause");
            Controls.Add(play);
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // keep it out of Alt+Tab and the taskbar
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Windows 11 draws real rounded corners; on Windows 10 we clip to a rounded shape instead.
        int round = Native.DWMWCP_ROUND;
        bool win11Rounded = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref round, sizeof(int)) == 0;
        if (!win11Rounded) RoundOff(this, 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private static void RoundOff(Control c, int radius)
    {
        var region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, c.Width + 1, c.Height + 1, radius, radius));
        c.Region?.Dispose();
        c.Region = region;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
