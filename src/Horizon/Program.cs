namespace Horizon;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "KamiUX.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("KAMI UX is already running — look for its icon in the system tray.", "KAMI UX");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);

        Application.Run(new TrayApp());
    }
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(HorizonConfig.Folder);
            File.AppendAllText(Path.Combine(HorizonConfig.Folder, "log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
