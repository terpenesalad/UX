using Microsoft.Win32;

namespace Horizon;

/// <summary>
/// The "own OS" look:
///  • Windows itself (Explorer, Settings, the Start menu, apps that follow the system) goes dark;
///  • every window gets the same minimal title bar: near-black, soft light text, rounded corners,
///    and no coloured border.
/// Everything is put back exactly as it was when KAMI UX quits.
/// </summary>
internal sealed class WindowTheme : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Colours are 0x00BBGGRR.
    private const int CaptionColor = 0x001E1C1C;   // #1C1C1E
    private const int CaptionText = 0x00EAE6E6;    // #E6E6EA

    private static string BackupPath => Path.Combine(HorizonConfig.Folder, "theme-backup.txt");

    private readonly HashSet<IntPtr> _themed = new();
    private HorizonConfig _cfg;

    public WindowTheme(HorizonConfig cfg)
    {
        _cfg = cfg;
        Apply();
    }

    public void UpdateConfig(HorizonConfig cfg)
    {
        bool chromeWasOn = _cfg.MinimalWindowChrome;
        _cfg = cfg;
        if (chromeWasOn && !cfg.MinimalWindowChrome) RevertAllWindows();
        Apply();
    }

    private void Apply()
    {
        if (_cfg.DarkMode) SetSystemDark();
        else RestoreSystemTheme();

        if (_cfg.MinimalWindowChrome)
            foreach (var h in Win.AppWindows(_cfg)) Style(h);
    }

    /// <summary>Call when a window appears (or comes to the front) so new windows match too.</summary>
    public void OnWindowShown(IntPtr hwnd)
    {
        if (!_cfg.MinimalWindowChrome || _themed.Contains(hwnd)) return;
        if (!Win.IsManageable(hwnd, _cfg)) return;
        Style(hwnd);
    }

    private void Style(IntPtr hwnd)
    {
        int on = 1, caption = CaptionColor, text = CaptionText, border = Native.DWMWA_COLOR_NONE, round = Native.DWMWCP_ROUND;
        // Setting another app's frame colours is allowed by DWM (it's how frame-tinting tools work).
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        _themed.Add(hwnd);
        if (_themed.Count > 2000) _themed.RemoveWhere(h => !Native.IsWindow(h));
    }

    private void RevertAllWindows()
    {
        int def = Native.DWMWA_COLOR_DEFAULT;
        foreach (var h in _themed.Where(Native.IsWindow))
        {
            int c = def, t = def, b = def;
            Native.DwmSetWindowAttribute(h, Native.DWMWA_CAPTION_COLOR, ref c, sizeof(int));
            Native.DwmSetWindowAttribute(h, Native.DWMWA_TEXT_COLOR, ref t, sizeof(int));
            Native.DwmSetWindowAttribute(h, Native.DWMWA_BORDER_COLOR, ref b, sizeof(int));
        }

        _themed.Clear();
    }

    // ── System dark mode ─────────────────────────────────────────────────────

    private static void SetSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            int apps = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1));
            int system = Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 1));
            if (apps == 0 && system == 0) return;

            if (!File.Exists(BackupPath))
            {
                Directory.CreateDirectory(HorizonConfig.Folder);
                File.WriteAllText(BackupPath, $"{apps},{system}");
            }

            key.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", 0, RegistryValueKind.DWord);
            Broadcast();
        }
        catch (Exception ex)
        {
            Log.Write("Could not switch to dark mode: " + ex.Message);
        }
    }

    public static void RestoreSystemTheme() => RestoreSystemTheme(false);

    private static void RestoreSystemTheme(bool quitting)
    {
        try
        {
            if (!File.Exists(BackupPath)) return;
            var parts = File.ReadAllText(BackupPath).Split(',');
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            if (parts.Length == 2 && int.TryParse(parts[0], out int apps) && int.TryParse(parts[1], out int system))
            {
                key.SetValue("AppsUseLightTheme", apps, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", system, RegistryValueKind.DWord);
                Broadcast(wait: quitting);
            }

            File.Delete(BackupPath);
        }
        catch (Exception ex)
        {
            Log.Write("Could not restore the theme: " + ex.Message);
        }
    }

    /// <summary>
    /// Tells running apps the colour mode changed so they switch without a restart. In the
    /// background normally (a slow app can't stall anything); quick and in-line when quitting.
    /// </summary>
    private static void Broadcast(bool wait = false)
    {
        void Send(uint timeout) => Native.SendMessageTimeoutString(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE,
            IntPtr.Zero, "ImmersiveColorSet", Native.SMTO_ABORTIFHUNG, timeout, out _);

        if (wait) Send(100);
        else Task.Run(() => Send(500));
    }

    public void Dispose()
    {
        RevertAllWindows();
        RestoreSystemTheme(quitting: true);
    }
}
