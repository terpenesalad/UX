using System.Text.Json;

namespace Horizon;

/// <summary>
/// User settings, stored as JSON in %APPDATA%\Horizon\config.json.
/// The file is created with these defaults on first run; edit it and choose
/// "Reload settings" from the tray menu.
/// </summary>
internal sealed class HorizonConfig
{
    /// <summary>Share of the screen width used by the centre focus zone (0.3 – 0.8).</summary>
    public double FocusWidthRatio { get; set; } = 0.5;

    /// <summary>How close (in pixels) to the screen edge you must drop a window to stash it.</summary>
    public int StashEdgePixels { get; set; } = 28;

    /// <summary>Gap between parked windows and the zone edges.</summary>
    public int Margin { get; set; } = 16;

    /// <summary>Parked windows are never made shorter than this.</summary>
    public int MinParkedHeight { get; set; } = 220;

    /// <summary>Show the neon zone overlay while you drag a window.</summary>
    public bool ShowZonesWhileDragging { get; set; } = true;

    /// <summary>Put every parked or stashed window back where it was when Horizon exits.</summary>
    public bool RestoreWindowsOnExit { get; set; } = true;

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon");

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
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<HorizonConfig>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null) return loaded.Clamped();
            }

            var defaults = new HorizonConfig();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
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
        StashEdgePixels = Math.Clamp(StashEdgePixels, 4, 200);
        Margin = Math.Clamp(Margin, 0, 100);
        MinParkedHeight = Math.Clamp(MinParkedHeight, 80, 2000);
        return this;
    }
}
