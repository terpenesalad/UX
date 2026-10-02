using System.Runtime.InteropServices;

namespace Horizon;

/// <summary>
/// The fluid drag. Horizon intercepts a press on a window's title bar; if the mouse then moves,
/// it hides the real window and drags a live preview instead, shrinking it the further toward the
/// side it goes ("pushed away"). On release, the real window is put down at the preview's size.
/// A press that doesn't turn into a drag is replayed untouched, so clicks and double-clicks work.
///
/// The mouse hook lives on its own thread and only flips a few flags, so the pointer can never
/// stutter, however busy the rest of Horizon is. All window work happens on the UI thread.
/// </summary>
internal sealed class DragController : IDisposable
{
    private enum HookState { Idle, Pressed, Dragging }

    private readonly ZoneManager _zones;
    private readonly Control _ui = new();
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 15 };
    private readonly Thread _hookThread;
    private Native.LowLevelMouseProc? _proc; // must stay referenced while hooked
    private IntPtr _hook;
    private uint _hookThreadId;

    // Owned by the hook thread.
    private HookState _hookState = HookState.Idle;
    private IntPtr _pressedWindow;
    private Point _downPoint;
    private bool _swallowRightUp;

    // Shared (written by the hook, read by the UI).
    private volatile bool _buttonHeld;
    private long _cursorPacked;
    private long _lastHookTick;

    // Owned by the UI thread.
    private bool _dragging;
    private IntPtr _target;
    private Rectangle _before;
    private Size _full;
    private PointF _grab;
    private double _scale;
    private ThumbnailView? _view;
    private Rectangle _viewRect;

    public volatile bool Enabled = true;

    public event Action<Point>? Moved;
    public event Action? Started;
    public event Action? Ended;

    public DragController(ZoneManager zones)
    {
        _zones = zones;
        _ = _ui.Handle; // create it now so the hook thread can post to the UI thread
        _frame.Tick += (_, _) => Frame();

        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "KAMI UX mouse hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private Point Cursor
    {
        get
        {
            long v = Interlocked.Read(ref _cursorPacked);
            return new Point((int)(v >> 32), (int)(v & 0xFFFFFFFF));
        }
        set => Interlocked.Exchange(ref _cursorPacked, ((long)value.X << 32) | (uint)value.Y);
    }

    // ── Hook thread ──────────────────────────────────────────────────────────

    private void HookThreadMain()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("Mouse hook failed: " + Marshal.GetLastWin32Error());
            return;
        }

        Application.Run(); // message loop for this thread; ends on WM_QUIT
        Native.UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Exchange(ref _lastHookTick, Environment.TickCount64);
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
                if (!injected && Handle(wParam.ToInt32(), new Point(info.pt.X, info.pt.Y)))
                    return (IntPtr)1; // swallow
            }
            catch (Exception ex)
            {
                Log.Write("Mouse hook error: " + ex.Message);
                _hookState = HookState.Idle;
                _buttonHeld = false;
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Runs on the hook thread. Returns true to swallow the event. Must stay quick.</summary>
    private bool Handle(int msg, Point pt)
    {
        if (msg == Native.WM_RBUTTONUP && _swallowRightUp)
        {
            _swallowRightUp = false;
            return true;
        }

        switch (_hookState)
        {
            case HookState.Idle when msg == Native.WM_LBUTTONDOWN:
                if (!IsTitleBar(pt, out var hwnd)) return false;
                _hookState = HookState.Pressed;
                _pressedWindow = hwnd;
                _downPoint = pt;
                _buttonHeld = true;
                return true;

            case HookState.Pressed when msg == Native.WM_MOUSEMOVE:
                var drag = SystemInformation.DragSize;
                if (Math.Abs(pt.X - _downPoint.X) > drag.Width || Math.Abs(pt.Y - _downPoint.Y) > drag.Height)
                {
                    _hookState = HookState.Dragging;
                    Cursor = pt;
                    var target = _pressedWindow;
                    var down = _downPoint;
                    _ui.BeginInvoke(new Action(() => BeginDrag(target, down)));
                }

                return false; // never block the pointer itself

            case HookState.Pressed when msg == Native.WM_LBUTTONUP:
                // Just a click: hand it to the window as if we were never here.
                _hookState = HookState.Idle;
                _buttonHeld = false;
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                return true;

            case HookState.Dragging when msg == Native.WM_MOUSEMOVE:
                Cursor = pt;
                return false;

            case HookState.Dragging when msg == Native.WM_LBUTTONUP:
                _hookState = HookState.Idle;
                Cursor = pt;
                _buttonHeld = false;
                _ui.BeginInvoke(new Action(Drop));
                return true;

            case HookState.Dragging when msg == Native.WM_RBUTTONDOWN:
                // Right-click while dragging cancels. Swallow its matching button-up too.
                _hookState = HookState.Idle;
                _swallowRightUp = true;
                _ui.BeginInvoke(new Action(Cancel));
                return true;
        }

        return false;
    }

    private bool IsTitleBar(Point pt, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!Enabled || _zones.Paused) return false;

        hwnd = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = pt.X, Y = pt.Y }), Native.GA_ROOT);
        if (!Win.IsManageable(hwnd, _zones.Config) || Native.IsZoomed(hwnd)) return false;

        // Ask the window what's under the pointer; only its title bar counts.
        var lParam = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
        if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK, 40, out IntPtr hit) == IntPtr.Zero) return false;
        return hit.ToInt64() == Native.HTCAPTION;
    }

    // ── UI thread ────────────────────────────────────────────────────────────

    private void BeginDrag(IntPtr target, Point down)
    {
        if (_dragging || !Native.IsWindow(target)) return;

        _target = target;
        _before = Win.GetVisibleBounds(target);
        _full = _zones.FullSizeOf(target);
        _grab = new PointF(
            Math.Clamp((down.X - _before.Left) / (float)Math.Max(1, _before.Width), 0, 1),
            Math.Clamp((down.Y - _before.Top) / (float)Math.Max(1, _before.Height), 0, 1));
        _scale = _before.Width / (double)Math.Max(1, _full.Width);

        _viewRect = _before;
        _view = new ThumbnailView(target, ThumbnailView.VisibleCrop(target)) { Bounds = _before };
        _view.Show();
        _view.Place(_before);
        Win.MoveOffscreen(target);

        _dragging = true;
        _frame.Start();
        Started?.Invoke();
        Frame();
    }

    /// <summary>~60 fps: ease the preview's scale toward the depth at the pointer.</summary>
    private void Frame()
    {
        if (!_dragging || _view == null) return;

        // Safety nets: a missed release drops in place; Esc cancels.
        if (!_buttonHeld)
        {
            Drop();
            return;
        }

        if (Native.IsKeyDown(Native.VK_ESCAPE))
        {
            _hookState = HookState.Idle; // benign race: worst case the next event is passed through
            _buttonHeld = false;
            Cancel();
            return;
        }

        // If Windows dropped our hook (it does that to hooks it thinks are slow), the pointer
        // moves without us hearing about it. Don't leave a frozen drag behind: drop it.
        if (Native.GetCursorPos(out var real))
        {
            var heard = Cursor;
            bool pointerMoved = Math.Abs(real.X - heard.X) > 4 || Math.Abs(real.Y - heard.Y) > 4;
            bool hookSilent = Environment.TickCount64 - Interlocked.Read(ref _lastHookTick) > 250;
            if (pointerMoved && hookSilent)
            {
                Log.Write("Mouse hook went quiet mid-drag; dropping the window.");
                _hookState = HookState.Idle;
                _buttonHeld = false;
                Cursor = new Point(real.X, real.Y);
                Drop();
                return;
            }
        }

        var cursor = Cursor;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        double target = Depth.Scale(cursor.X, wa, _zones.Config);
        bool atEdge = _zones.StashEdgeAt(cursor) != null;
        if (atEdge) target = Math.Min(target, 0.25); // hint: it'll tuck away

        _scale += (target - _scale) * 0.25;
        _viewRect = Depth.ScaledAround(_full, _scale, cursor, _grab);
        _view.Place(_viewRect, atEdge ? (byte)170 : (byte)255);
        _zones.ReportDrag(_viewRect);
        Moved?.Invoke(cursor);
    }

    private void Drop()
    {
        if (!_dragging || _view == null) return;
        _frame.Stop();
        _dragging = false;

        var cursor = Cursor;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        double scale = Depth.Scale(cursor.X, wa, _zones.Config);
        var finalRect = _zones.StashEdgeAt(cursor) != null
            ? _viewRect
            : Depth.ScaledAround(_full, scale, cursor, _grab); // the settled size, not mid-ease

        var view = _view;
        _view = null;
        Ended?.Invoke();

        try
        {
            _zones.CompleteDrag(_target, cursor, finalRect, _full, _before, view);
        }
        catch (Exception ex)
        {
            Log.Write("Drop failed: " + ex);
            Glide.Land(_target, _before, activate: false, view);
        }
    }

    private void Cancel()
    {
        if (!_dragging || _view == null) return;
        _frame.Stop();
        _dragging = false;

        var view = _view;
        _view = null;
        Ended?.Invoke();
        _zones.ReportDrag(null, dropped: true);
        Glide.Land(_target, _before, activate: false, view);
    }

    public void Dispose()
    {
        Cancel();
        _frame.Dispose();
        if (_hookThreadId != 0) Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(500);
        _ui.Dispose();
    }
}
