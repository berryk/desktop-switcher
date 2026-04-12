namespace DesktopSwitcher;

public static class Log
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "desktop-switcher.log");

    private static readonly object Lock = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        lock (Lock)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public static void Clear()
    {
        try { File.Delete(LogPath); } catch { }
    }
}
