using System.Text.Json;

namespace Horizon;

/// <summary>
/// User settings, stored as JSON in %APPDATA%\KAMI UX\config.json.
/// The file is created with these defaults on first run; edit it and choose
/// "Reload settings" from the tray menu. Missing settings fall back to these defaults.
/// </summary>
internal sealed class HorizonConfig
{
    // Zones
    /// <summary>Share of the screen width used by the centre focus zone (0.3 – 0.8).</summary>
    public double FocusWidthRatio { get; set; } = 0.5;

    /// <summary>How small a window gets at the far edge of the periphery (0.2 – 0.9 of its full size).</summary>
    public double PeripheryMinScale { get; set; } = 0.45;

    /// <summary>How close (in pixels) to the screen edge you must drop a window to stash it.</summary>
    public int StashEdgePixels { get; set; } = 28;

    /// <summary>Gap kept around windows Horizon places.</summary>
    public int Margin { get; set; } = 16;

    // Behaviour
    /// <summary>Horizon handles title-bar drags itself so windows shrink smoothly with depth.</summary>
    public bool FluidDrag { get; set; } = true;

    /// <summary>Glide windows into place when using shortcuts, the dock or stash widgets.</summary>
    public bool AnimateMoves { get; set; } = true;

    /// <summary>Show the zone map while dragging (the wallpaper already shows the zones).</summary>
    public bool ShowZonesWhileDragging { get; set; } = false;

    /// <summary>Put every moved window back where it was when Horizon exits.</summary>
    public bool RestoreWindowsOnExit { get; set; } = true;

    // Look
    /// <summary>Generate the dark grid wallpaper for each monitor and set it while Horizon runs.</summary>
    public bool SetWallpaper { get; set; } = true;

    /// <summary>Put your previous wallpaper back when Horizon exits.</summary>
    public bool RestoreWallpaperOnExit { get; set; } = true;

    /// <summary>Show the Horizon dock (built from your pinned taskbar apps).</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>Hide the Windows taskbar completely while KAMI UX runs (restored on exit).</summary>
    public bool AutoHideTaskbar { get; set; } = true;

    /// <summary>The grid behind your windows glows where you move them (otherwise it's a still wallpaper).</summary>
    public bool LiveGrid { get; set; } = true;

    /// <summary>Window edges click onto nearby grid lines when you let go.</summary>
    public bool SnapToGrid { get; set; } = true;
    public int SnapDistance { get; set; } = 14;

    /// <summary>Switch Windows (Explorer, Settings, apps that follow it) to dark mode while KAMI UX runs.</summary>
    public bool DarkMode { get; set; } = true;

    /// <summary>Give every window the same minimal dark title bar, with no coloured border.</summary>
    public bool MinimalWindowChrome { get; set; } = true;

    /// <summary>Dock icon size at rest, and when magnified under the pointer.</summary>
    public int DockIconSize { get; set; } = 52;
    public int DockMagnifiedSize { get; set; } = 84;

    // Video screens
    /// <summary>
    /// Keeps browsers rendering video behind a video screen (they pause hidden windows otherwise).
    /// Turn off if a video screen ever shows up blank.
    /// </summary>
    public bool KeepVideoSourcesAwake { get; set; } = true;

    // Lists
    /// <summary>Apps whose stash widget gets a play/pause button (matched against the process name).</summary>
    public string[] MediaApps { get; set; } =
    {
        "spotify", "music", "vlc", "foobar2000", "itunes", "tidal", "deezer", "winamp", "aimp", "musicbee"
    };

    /// <summary>Processes Horizon never touches.</summary>
    public string[] IgnoreProcesses { get; set; } =
    {
        "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "TextInputHost"
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KAMI UX");

    public static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static HorizonConfig Load()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            HorizonConfig cfg = new();
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<HorizonConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new();

            // Re-save so settings added in newer versions appear in the file.
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg.Clamped(), JsonOptions));
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Write("Could not read config, using defaults: " + ex.Message);
            return new HorizonConfig();
        }
    }

    private HorizonConfig Clamped()
    {
        FocusWidthRatio = Math.Clamp(FocusWidthRatio, 0.3, 0.8);
        PeripheryMinScale = Math.Clamp(PeripheryMinScale, 0.2, 0.9);
        StashEdgePixels = Math.Clamp(StashEdgePixels, 4, 200);
        Margin = Math.Clamp(Margin, 0, 100);
        DockIconSize = Math.Clamp(DockIconSize, 32, 96);
        DockMagnifiedSize = Math.Clamp(DockMagnifiedSize, DockIconSize, 160);
        SnapDistance = Math.Clamp(SnapDistance, 0, 60);
        return this;
    }
}
