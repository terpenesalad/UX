using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace Horizon;

/// <summary>
/// KAMI Files: a calm, dark, Finder-style file browser. A sidebar of places, a breadcrumb path,
/// big icons and picture thumbnails (or a list), instant filtering and deep search. It does the
/// everyday things — open, rename, copy/cut/paste, drag and drop, new folder, Recycle Bin —
/// using Windows' own file operations, so progress, conflicts and undo work as usual.
/// </summary>
internal sealed class KamiFiles : Form
{
    private const int IconSize = 64;
    private const int ListIcon = 20;

    private static readonly List<KamiFiles> Windows = new();

    public static bool AnyOpen => Windows.Any(w => !w.IsDisposed && w.Visible);

    /// <summary>Brings the last Files window forward, or opens one.</summary>
    public static void OpenOrFocus()
    {
        var existing = Windows.LastOrDefault(w => !w.IsDisposed);
        if (existing != null) existing.BringIn();
        else OpenNew(null);
    }

    public static void OpenNew(string? path)
    {
        var w = new KamiFiles(path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        w.Show();
        w.BringIn();
    }

    /// <summary>Set by the app so Files can come back from the sides like any other window.</summary>
    public static ZoneManager? Zones { get; set; }

    private void BringIn()
    {
        if (Zones != null && Zones.IsTracked(Handle))
        {
            Zones.BringToFocus(Handle);
            return;
        }

        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Win.ForceForeground(Handle);
    }

    // ── State ────────────────────────────────────────────────────────────────

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required bool IsDir { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public string Kind { get; init; } = "";
        public string IconKey { get; set; } = "";
    }

    private sealed record Place(string Name, string? Path, string Glyph, bool Header = false);

    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _current = "";
    private List<Entry> _entries = new();
    private bool _searchResults;
    private bool _bigIcons = true;
    private bool _renaming;
    private Entry? _renameEntry; // the file being renamed (the list can change underneath the edit box)
    private readonly Dictionary<string, Bitmap> _icons = new();

    private readonly ListBox _sidebar;
    private readonly List<Place> _places = new();
    private readonly Button _back, _forward, _up, _viewToggle;
    private readonly FlowLayoutPanel _crumbs;
    private readonly TextBox _pathBox;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly ImageList _images = new() { ImageSize = new Size(IconSize, IconSize), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ImageList _rowHeight = new() { ImageSize = new Size(1, 28) };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 900 };
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _work;

    private static readonly string GlyphFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static readonly HashSet<string> PerFileIcons = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };

    private static readonly HashSet<string> Thumbnails = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".pdf", ".psd" };

    public KamiFiles(string start)
    {
        Text = "Files";
        Size = new Size(1180, 740);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Kami.Bg;
        ForeColor = Kami.Text;
        Font = Kami.Font(10f);
        KeyPreview = true;
        Icon = Icon.FromHandle(BuiltinIcon.Files(32).GetHicon());
        Kami.DarkTitleBar(this);
        Win.RegisterAppWindow(this);
        Windows.Add(this);

        // ── Main list ──
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            BackColor = Kami.Bg,
            ForeColor = Kami.Text,
            BorderStyle = BorderStyle.None,
            View = View.LargeIcon,
            LargeImageList = _images,
            SmallImageList = _rowHeight,
            OwnerDraw = true,
            LabelEdit = true,
            AllowDrop = true,
            HideSelection = false,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _list.Columns.Add("Name", 420);
        _list.Columns.Add("Date modified", 170);
        _list.Columns.Add("Kind", 160);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        Kami.DarkControl(_list);
        _list.DrawItem += DrawItem;
        _list.DrawSubItem += DrawSubItem;
        _list.DrawColumnHeader += DrawHeader;
        _list.ItemActivate += (_, _) => OpenSelected();
        _list.BeforeLabelEdit += (_, e) =>
        {
            _renaming = true;
            _renameEntry = e.Item >= 0 && e.Item < _list.Items.Count ? _list.Items[e.Item].Tag as Entry : null;
        };
        _list.AfterLabelEdit += (s, e) =>
        {
            _renaming = false;
            Rename(s, e);
        };
        _list.ItemDrag += (_, _) => StartDrag();
        _list.DragEnter += DragOverList;
        _list.DragOver += DragOverList;
        _list.DragDrop += DropOnList;
        _list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(e.Location); };
        _list.SelectedIndexChanged += (_, _) => UpdateStatus();
        _list.HandleCreated += (_, _) => SetIconSpacing();

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            ForeColor = Kami.Sub,
            BackColor = Kami.Bg,
            Padding = new Padding(16, 6, 0, 0),
            Font = Kami.Font(9f)
        };

        // ── Top bar ──
        var top = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Kami.Bg, Padding = new Padding(10, 10, 12, 10) };
        _back = GlyphButton("", "Back", (_, _) => GoHistory(-1));
        _forward = GlyphButton("", "Forward", (_, _) => GoHistory(+1));
        _up = GlyphButton("", "Enclosing folder", (_, _) => GoUp());
        _viewToggle = GlyphButton("", "Show as list", (_, _) => ToggleView());

        _search = new TextBox
        {
            Dock = DockStyle.Right,
            Width = 240,
            BackColor = Kami.Panel,
            ForeColor = Kami.Text,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Search",
            Font = Kami.Font(10.5f)
        };
        _search.TextChanged += (_, _) => { if (!_searchResults) ShowEntries(); };
        _search.KeyDown += SearchKeys;

        _crumbs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Kami.Bg,
            WrapContents = false,
            AutoScroll = false,
            Padding = new Padding(8, 4, 0, 0),
            Cursor = Cursors.IBeam
        };
        _crumbs.Click += (_, _) => EditPath();

        _pathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Kami.Panel,
            ForeColor = Kami.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Kami.Font(10.5f)
        };
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; HidePathBox(); Navigate(_pathBox.Text.Trim().Trim('"')); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; HidePathBox(); }
        };
        _pathBox.Leave += (_, _) => HidePathBox();

        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 132, BackColor = Kami.Bg, WrapContents = false };
        nav.Controls.AddRange(new Control[] { _back, _forward, _up });
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 44, BackColor = Kami.Bg, WrapContents = false };
        right.Controls.Add(_viewToggle);

        top.Controls.Add(_crumbs);
        top.Controls.Add(_pathBox);
        top.Controls.Add(right);
        top.Controls.Add(_search);
        top.Controls.Add(nav);

        // ── Sidebar ──
        _sidebar = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 210,
            BackColor = Color.FromArgb(24, 24, 27),
            ForeColor = Kami.Text,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 32,
            IntegralHeight = false
        };
        Kami.DarkControl(_sidebar);
        _sidebar.DrawItem += DrawPlace;
        _sidebar.MouseDown += (_, e) =>
        {
            int i = _sidebar.IndexFromPoint(e.Location);
            if (i >= 0 && i < _places.Count && _places[i].Path is string p) Navigate(p);
        };
        BuildPlaces();

        // Docking order: fill first, edges after.
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Kami.Bg };
        main.Controls.Add(_list);
        main.Controls.Add(_status);
        main.Controls.Add(top);
        Controls.Add(main);
        Controls.Add(_sidebar);

        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (!_searchResults && !_renaming) Reload(keepSelection: true);
        };

        Navigate(start);
    }

    // ── Places ───────────────────────────────────────────────────────────────

    private void BuildPlaces()
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string downloads = Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads");

        _places.Add(new Place("Favourites", null, "", Header: true));
        _places.Add(new Place("Home", Folder(Environment.SpecialFolder.UserProfile), ""));
        _places.Add(new Place("Desktop", Folder(Environment.SpecialFolder.DesktopDirectory), ""));
        _places.Add(new Place("Documents", Folder(Environment.SpecialFolder.MyDocuments), ""));
        _places.Add(new Place("Downloads", downloads, ""));
        _places.Add(new Place("Pictures", Folder(Environment.SpecialFolder.MyPictures), ""));
        _places.Add(new Place("Music", Folder(Environment.SpecialFolder.MyMusic), ""));
        _places.Add(new Place("Videos", Folder(Environment.SpecialFolder.MyVideos), ""));

        _places.Add(new Place("Locations", null, "", Header: true));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // Network drives can take ages to answer when disconnected — keep the window snappy.
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                _places.Add(new Place($"{label} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName, ""));
            }
            catch
            {
                // Drive vanished or not accessible.
            }
        }

        _sidebar.Items.Clear();
        foreach (var p in _places) _sidebar.Items.Add(p.Name);
    }

    private void DrawPlace(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _places.Count) return;
        var p = _places[e.Index];
        var g = e.Graphics;
        using (var bg = new SolidBrush(_sidebar.BackColor)) g.FillRectangle(bg, e.Bounds);

        if (p.Header)
        {
            TextRenderer.DrawText(g, p.Name, Kami.Font(8.5f, FontStyle.Bold), new Rectangle(e.Bounds.X + 16, e.Bounds.Y + 10, e.Bounds.Width, 20),
                Kami.Sub, TextFormatFlags.Left);
            return;
        }

        bool here = p.Path != null && string.Equals(Path.TrimEndingDirectorySeparator(p.Path), Path.TrimEndingDirectorySeparator(_current), StringComparison.OrdinalIgnoreCase);
        if (here)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Kami.RoundRect(new RectangleF(e.Bounds.X + 8, e.Bounds.Y + 2, e.Bounds.Width - 16, e.Bounds.Height - 4), 7);
            using var sel = new SolidBrush(Kami.Selected);
            g.FillPath(sel, path);
        }

        using var glyphFont = new Font(GlyphFont, 11f);
        TextRenderer.DrawText(g, p.Glyph, glyphFont, new Rectangle(e.Bounds.X + 18, e.Bounds.Y, 24, e.Bounds.Height), Kami.Sub,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        TextRenderer.DrawText(g, p.Name, Kami.Font(10f), new Rectangle(e.Bounds.X + 46, e.Bounds.Y, e.Bounds.Width - 50, e.Bounds.Height),
            Kami.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void Navigate(string path, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch { return; }

        if (File.Exists(path))
        {
            OpenPath(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            _status.Text = "Can't find " + path;
            return;
        }

        _current = path;
        if (remember)
        {
            if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(path);
            _historyIndex = _history.Count - 1;
        }

        _searchResults = false;
        _search.Text = "";
        Reload(keepSelection: false);
        BuildCrumbs();
        Watch(path);
        Text = DisplayName(path);
        _back.Enabled = _historyIndex > 0;
        _forward.Enabled = _historyIndex < _history.Count - 1;
        _up.Enabled = Directory.GetParent(path) != null;
        _sidebar.Invalidate();
    }

    private void GoHistory(int delta)
    {
        int i = _historyIndex + delta;
        if (i < 0 || i >= _history.Count) return;
        _historyIndex = i;
        Navigate(_history[i], remember: false);
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_current);
        if (parent != null) Navigate(parent.FullName);
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void BuildCrumbs()
    {
        _crumbs.SuspendLayout();
        _crumbs.Controls.Clear();

        var parts = new List<(string Name, string Path)>();
        for (var d = new DirectoryInfo(_current); d != null; d = d.Parent)
            parts.Insert(0, (DisplayName(d.FullName), d.FullName));

        // Long paths: keep the last few.
        if (parts.Count > 5) parts = parts.Skip(parts.Count - 5).ToList();

        for (int i = 0; i < parts.Count; i++)
        {
            var (name, path) = parts[i];
            bool last = i == parts.Count - 1;
            var b = new Button
            {
                Text = name,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = last ? Kami.Text : Kami.Sub,
                BackColor = Kami.Bg,
                Font = Kami.Font(last ? 11f : 10.5f, last ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(0),
                Padding = new Padding(2, 0, 2, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Kami.PanelHover;
            b.Click += (_, _) => Navigate(path);
            _crumbs.Controls.Add(b);

            if (!last)
                _crumbs.Controls.Add(new Label { Text = "›", AutoSize = true, ForeColor = Kami.Sub, Margin = new Padding(0, 6, 0, 0), Font = Kami.Font(11f) });
        }

        _crumbs.ResumeLayout();
    }

    private void EditPath()
    {
        _pathBox.Text = _current;
        _crumbs.Visible = false;
        _pathBox.Visible = true;
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    private void HidePathBox()
    {
        _pathBox.Visible = false;
        _crumbs.Visible = true;
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    private void Reload(bool keepSelection)
    {
        var selected = keepSelection ? SelectedPaths().ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        _entries = ReadFolder(_current);
        ShowEntries(selected);
    }

    private List<Entry> ReadFolder(string path)
    {
        var list = new List<Entry>();
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                try
                {
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    list.Add(ToEntry(info));
                }
                catch
                {
                    // Unreadable entry: skip it.
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _status.Text = "You don't have permission to see this folder.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }

        list.Sort(Compare);
        return list;
    }

    private static Entry ToEntry(FileSystemInfo info)
    {
        bool dir = info is DirectoryInfo;
        string ext = dir ? "" : info.Extension;
        return new Entry
        {
            Path = info.FullName,
            Name = info.Name,
            IsDir = dir,
            Size = info is FileInfo f ? f.Length : 0,
            Modified = info.LastWriteTime,
            Kind = dir ? "Folder" : string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file",
            IconKey = dir ? "folder" : PerFileIcons.Contains(ext) || Thumbnails.Contains(ext) ? "path:" + info.FullName : "ext:" + ext.ToLowerInvariant()
        };
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Folders first, then names the way people expect ("2" before "10").</summary>
    private static int Compare(Entry a, Entry b) =>
        a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : StrCmpLogicalW(a.Name, b.Name);

    private void ShowEntries(HashSet<string>? select = null)
    {
        string filter = _search.Text.Trim();
        var shown = filter.Length == 0 || _searchResults
            ? _entries
            : _entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in shown)
        {
            var item = new ListViewItem(e.Name) { Tag = e, ImageKey = e.IconKey };
            item.SubItems.Add(e.Modified.ToString("d MMM yyyy, h:mm tt"));
            item.SubItems.Add(e.Kind);
            item.SubItems.Add(e.IsDir ? "—" : FormatSize(e.Size));
            if (select != null && select.Contains(e.Path)) item.Selected = true;
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        UpdateStatus();
        LoadIcons(shown);
    }

    private void UpdateStatus()
    {
        int n = _list.Items.Count, s = _list.SelectedItems.Count;
        string where = _searchResults ? " found" : "";
        _status.Text = s > 0 ? $"{s} of {n} selected" : $"{n} item{(n == 1 ? "" : "s")}{where}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[u]}";
    }

    /// <summary>Fetches icons and thumbnails on a background thread, filling them in as they arrive.</summary>
    private void LoadIcons(List<Entry> entries)
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();

        var wanted = new List<(string Key, string Sample, bool IconOnly)>();
        var seen = new HashSet<string>();
        foreach (var e in entries)
        {
            if (_icons.ContainsKey(e.IconKey) || !seen.Add(e.IconKey)) continue;
            bool thumb = !e.IsDir && Thumbnails.Contains(System.IO.Path.GetExtension(e.Path));
            wanted.Add((e.IconKey, e.Path, !thumb));
        }

        if (wanted.Count == 0) return;

        var worker = new Thread(() =>
        {
            foreach (var (key, sample, iconOnly) in wanted)
            {
                if (cts.IsCancellationRequested) return;
                var bmp = Win.ShellImage(sample, IconSize, iconOnly) ?? Win.ShellIcon(sample, IconSize);
                if (bmp == null) continue;
                try
                {
                    if (!IsHandleCreated || IsDisposed) { bmp.Dispose(); return; }
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || _icons.ContainsKey(key)) { bmp.Dispose(); return; }
                        _icons[key] = bmp;
                        _list.Invalidate();
                    }));
                }
                catch (Exception)
                {
                    bmp.Dispose();
                    return; // window closed
                }
            }
        })
        { IsBackground = true, Name = "KAMI Files icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, // not every write: downloads would thrash
                SynchronizingObject = this
            };
            FileSystemEventHandler changed = (_, _) => { _refresh.Stop(); _refresh.Start(); };
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => { _refresh.Stop(); _refresh.Start(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Some locations (e.g. network roots) can't be watched; refresh with F5.
        }
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private void SearchKeys(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            _search.Text = "";
            if (_searchResults) Navigate(_current, remember: false);
            _list.Focus();
        }
        else if (e.KeyCode == Keys.Enter && _search.Text.Trim().Length > 0)
        {
            e.SuppressKeyPress = true;
            DeepSearch(_search.Text.Trim());
        }
        else if (e.KeyCode == Keys.Down)
        {
            _list.Focus();
        }
    }

    /// <summary>Searches everything inside the current folder (names only), in the background.</summary>
    private void DeepSearch(string query)
    {
        query = new string(query.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && c != '*' && c != '?').ToArray()).Trim();
        if (query.Length == 0 || query.Contains("..")) return;
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        string root = _current;
        _status.Text = $"Searching {DisplayName(root)}…";

        Task.Run(() =>
        {
            var found = new List<Entry>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            foreach (var p in Directory.EnumerateFileSystemEntries(root, "*" + query + "*", options))
            {
                if (cts.IsCancellationRequested) return found;
                try
                {
                    FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                    found.Add(ToEntry(info));
                }
                catch
                {
                    // Vanished while searching.
                }

                if (found.Count >= 1000) break;
            }

            found.Sort(Compare);
            return found;
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || IsDisposed) return;
            if (t.IsFaulted)
            {
                BeginInvoke(new Action(() => _status.Text = "Search failed: " + t.Exception?.GetBaseException().Message));
                return;
            }

            BeginInvoke(new Action(() =>
            {
                _searchResults = true;
                _entries = t.Result;
                ShowEntries();
                _status.Text = $"{_entries.Count}{(_entries.Count >= 1000 ? "+" : "")} results for “{query}” in {DisplayName(root)} — Esc to go back";
            }));
        }, TaskScheduler.Default);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private IEnumerable<string> SelectedPaths() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => ((Entry)i.Tag!).Path);

    private void OpenSelected()
    {
        var items = _list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag!).ToList();
        if (items.Count == 1 && items[0].IsDir)
        {
            Navigate(items[0].Path);
            return;
        }

        foreach (var e in items.Take(20))
        {
            if (e.IsDir) OpenNew(e.Path);
            else OpenPath(e.Path);
        }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Couldn't open {path}: {ex.Message}"); }
    }

    private void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Write("Couldn't open Explorer: " + ex.Message); }
    }

    private void CopyToClipboard(bool cut)
    {
        var paths = SelectedPaths().ToArray();
        if (paths.Length == 0) return;
        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
        _status.Text = cut ? $"Cut {paths.Length} item(s) — paste to move" : $"Copied {paths.Length} item(s)";
    }

    private void Paste()
    {
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && (ms.ReadByte() & 2) != 0;
        Transfer(files, _current, move);
        if (move) Clipboard.Clear();
    }

    /// <summary>
    /// Copies or moves with Windows' own progress and "replace or skip" dialogs — on a background
    /// thread, so a long copy never freezes the rest of KAMI UX.
    /// </summary>
    private void Transfer(IEnumerable<string> sources, string destination, bool move)
    {
        var list = sources.ToList();
        RunFileWork(() => TransferNow(list, destination, move));
    }

    /// <summary>Runs file work on its own STA thread (Windows' file dialogs need one), then refreshes.</summary>
    private void RunFileWork(Action work)
    {
        var t = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { Log.Write("File operation failed: " + ex.Message); }

            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Reload(keepSelection: true)));
            }
            catch (Exception)
            {
                // Window closed meanwhile.
            }
        })
        { IsBackground = false, Name = "KAMI Files operation" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static void TransferNow(List<string> sources, string destination, bool move)
    {
        foreach (var src in sources)
        {
            try
            {
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
                string target = Path.Combine(destination, name);
                bool same = string.Equals(Path.GetFullPath(src), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
                if (same && move) continue;
                if (same) target = UniqueName(destination, Path.GetFileNameWithoutExtension(name) + " copy" + Path.GetExtension(name));

                if (Directory.Exists(src))
                {
                    if (move) FileSystem.MoveDirectory(src, target, UIOption.AllDialogs);
                    else FileSystem.CopyDirectory(src, target, UIOption.AllDialogs);
                }
                else if (File.Exists(src))
                {
                    if (move) FileSystem.MoveFile(src, target, UIOption.AllDialogs);
                    else FileSystem.CopyFile(src, target, UIOption.AllDialogs);
                }
            }
            catch (OperationCanceledException)
            {
                return; // you pressed Cancel
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void RecycleSelected()
    {
        var paths = SelectedPaths().ToList();
        if (paths.Count > 0) RunFileWork(() => RecycleNow(paths));
    }

    private static void RecycleNow(List<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                // AllDialogs: if something can't go to the Recycle Bin (USB/network drives),
                // Windows asks before deleting it for good.
                if (Directory.Exists(p)) FileSystem.DeleteDirectory(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void NewFolder()
    {
        string path = UniqueName(_current, "New folder");
        try
        {
            Directory.CreateDirectory(path);
            Reload(keepSelection: false);
            var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((Entry)i.Tag!).Path == path);
            if (item != null)
            {
                item.Selected = true;
                item.EnsureVisible();
                item.BeginEdit();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string UniqueName(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    private void Rename(object? sender, LabelEditEventArgs e)
    {
        var entry = _renameEntry;
        _renameEntry = null;
        if (e.Label == null || entry == null) return; // edit cancelled
        string newName = e.Label.Trim();
        if (newName.Length == 0 || newName == entry.Name || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            e.CancelEdit = true;
            return;
        }

        try
        {
            if (entry.IsDir) FileSystem.RenameDirectory(entry.Path, newName);
            else FileSystem.RenameFile(entry.Path, newName);
        }
        catch (Exception ex)
        {
            e.CancelEdit = true;
            MessageBox.Show(this, ex.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, int shopObjectType, string pszObjectName, string? pszPropertyPage);

    private void ShowMenu(Point at)
    {
        var menu = KamiMenu.Create();
        var hit = _list.HitTest(at).Item;
        if (hit != null && !hit.Selected)
        {
            _list.SelectedItems.Clear();
            hit.Selected = true;
        }

        var paths = SelectedPaths().ToList();
        if (paths.Count > 0)
        {
            menu.Items.Add("Open", null, (_, _) => OpenSelected());
            menu.Items.Add("Show in File Explorer", null, (_, _) => ShowInExplorer(paths[0]));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy", null, (_, _) => CopyToClipboard(cut: false));
            menu.Items.Add("Cut", null, (_, _) => CopyToClipboard(cut: true));
            menu.Items.Add("Copy path", null, (_, _) => Clipboard.SetText(string.Join(Environment.NewLine, paths)));
            if (paths.Count == 1) menu.Items.Add("Rename", null, (_, _) => _list.SelectedItems[0].BeginEdit());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Move to Recycle Bin", null, (_, _) => RecycleSelected());
            if (paths.Count == 1)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Properties", null, (_, _) => SHObjectProperties(Handle, 2, paths[0], null));
            }
        }
        else
        {
            menu.Items.Add("New folder", null, (_, _) => NewFolder());
            var paste = menu.Items.Add("Paste", null, (_, _) => Paste());
            paste.Enabled = Clipboard.ContainsFileDropList();
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open in File Explorer", null, (_, _) => OpenPath(_current));
            menu.Items.Add("Refresh", null, (_, _) => Reload(keepSelection: true));
        }

        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_list, at);
    }

    // ── Drag and drop ────────────────────────────────────────────────────────

    private void StartDrag()
    {
        var paths = SelectedPaths().ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        _list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    private void DragOverList(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true || _searchResults)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        string target = DropTarget(e);
        bool sameDrive = files.Length > 0 && string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        bool ctrl = (e.KeyState & 8) != 0, shift = (e.KeyState & 4) != 0;
        e.Effect = ctrl ? DragDropEffects.Copy : shift || sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void DropOnList(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        string target = DropTarget(e);
        bool move = e.Effect == DragDropEffects.Move;

        // Never into itself or its own subfolder, and never "move" to where it already is.
        string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var valid = files.Where(f =>
        {
            string src = Norm(f), dest = Norm(target);
            bool intoSelf = dest.Equals(src, StringComparison.OrdinalIgnoreCase) ||
                            dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool alreadyThere = string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase);
            return !intoSelf && !(move && alreadyThere);
        }).ToList();

        e.Effect = DragDropEffects.None; // we do the move ourselves; the source must not also delete
        if (valid.Count == 0) return;
        BeginInvoke(new Action(() => Transfer(valid, target, move)));
    }

    /// <summary>Dropping onto a folder puts things inside it; anywhere else means here.</summary>
    private string DropTarget(DragEventArgs e)
    {
        var p = _list.PointToClient(new Point(e.X, e.Y));
        if (_list.HitTest(p).Item?.Tag is Entry { IsDir: true } dir) return dir.Path;
        return _current;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool editing = _pathBox.Focused || _search.Focused;
        switch (keyData)
        {
            case Keys.Alt | Keys.Left: GoHistory(-1); return true;
            case Keys.Alt | Keys.Right: GoHistory(+1); return true;
            case Keys.Alt | Keys.Up: GoUp(); return true;
            case Keys.Control | Keys.F: _search.Focus(); _search.SelectAll(); return true;
            case Keys.Control | Keys.L: EditPath(); return true;
            case Keys.Control | Keys.N: OpenNew(_current); return true;
            case Keys.Control | Keys.W: Close(); return true;
            case Keys.Control | Keys.Shift | Keys.N: NewFolder(); return true;
            case Keys.F5: Reload(keepSelection: true); return true;
        }

        if (!editing && _list.Focused)
        {
            switch (keyData)
            {
                case Keys.Back: GoHistory(-1); return true;
                case Keys.Delete: RecycleSelected(); return true;
                case Keys.F2: if (_list.SelectedItems.Count == 1) _list.SelectedItems[0].BeginEdit(); return true;
                case Keys.Control | Keys.C: CopyToClipboard(cut: false); return true;
                case Keys.Control | Keys.X: CopyToClipboard(cut: true); return true;
                case Keys.Control | Keys.V: Paste(); return true;
                case Keys.Control | Keys.A:
                    foreach (ListViewItem i in _list.Items) i.Selected = true;
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Views & drawing ──────────────────────────────────────────────────────

    private void ToggleView()
    {
        _bigIcons = !_bigIcons;
        _list.View = _bigIcons ? View.LargeIcon : View.Details;
        _viewToggle.Text = _bigIcons ? "" : "";
        _viewToggle.AccessibleName = _bigIcons ? "Show as list" : "Show as icons";
        if (_bigIcons) SetIconSpacing();
        _list.Invalidate();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void SetIconSpacing()
    {
        const int LVM_SETICONSPACING = 0x1035;
        SendMessage(_list.Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((128 << 16) | 112));
    }

    private void DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (_list.View != View.LargeIcon) return; // the list view is drawn per cell
        var g = e.Graphics;
        var entry = (Entry)e.Item.Tag!;
        var r = e.Bounds;

        using (var bg = new SolidBrush(Kami.Bg)) g.FillRectangle(bg, r);
        if (e.Item.Selected)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Kami.RoundRect(new RectangleF(r.X + 4, r.Y + 2, r.Width - 8, r.Height - 4), 10);
            using var sel = new SolidBrush(Kami.Selected);
            g.FillPath(sel, path);
        }

        _icons.TryGetValue(entry.IconKey, out var icon);
        var iconRect = new Rectangle(r.X + (r.Width - IconSize) / 2, r.Y + 8, IconSize, IconSize);
        if (icon != null) g.DrawImage(icon, iconRect);

        var textRect = new Rectangle(r.X + 6, iconRect.Bottom + 6, r.Width - 12, r.Bottom - iconRect.Bottom - 8);
        TextRenderer.DrawText(g, entry.Name, Kami.Font(9.5f), textRect, Kami.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (_list.View != View.Details || e.Item == null || e.SubItem == null) return;
        var g = e.Graphics;
        var entry = (Entry)e.Item.Tag!;
        var r = e.Bounds;

        using (var bg = new SolidBrush(e.Item.Selected ? Kami.Selected : Kami.Bg)) g.FillRectangle(bg, r);

        if (e.ColumnIndex == 0)
        {
            _icons.TryGetValue(entry.IconKey, out var icon);
            if (icon != null) g.DrawImage(icon, new Rectangle(r.X + 10, r.Y + (r.Height - ListIcon) / 2, ListIcon, ListIcon));
            TextRenderer.DrawText(g, entry.Name, _list.Font, new Rectangle(r.X + 38, r.Y, r.Width - 40, r.Height), Kami.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        else
        {
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.ColumnIndex == 3 ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(g, e.SubItem.Text, _list.Font, Rectangle.Inflate(r, -8, 0), Kami.Sub, flags);
        }
    }

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(Kami.Bg)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var line = new Pen(Kami.Line)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var flags = TextFormatFlags.VerticalCenter | (e.ColumnIndex == 3 ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Kami.Font(9f), Rectangle.Inflate(e.Bounds, -8, 0), Kami.Sub, flags);
    }

    private Button GlyphButton(string glyph, string name, EventHandler onClick)
    {
        var b = new Button
        {
            Text = glyph,
            Font = new Font(GlyphFont, 11f),
            Size = new Size(38, 34),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Kami.Text,
            BackColor = Kami.Bg,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 4, 0),
            AccessibleName = name
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Kami.PanelHover;
        new ToolTip().SetToolTip(b, name);
        b.Click += onClick;
        return b;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Windows.Remove(this);
        _work?.Cancel();
        _watcher?.Dispose();
        _refresh.Dispose();
        foreach (var b in _icons.Values) b.Dispose();
        _icons.Clear();
        base.OnFormClosed(e);
    }
}
